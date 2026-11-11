package main

import (
	"crypto/rand"
	"database/sql"
	"embed"
	"encoding/json"
	"fmt"
	"io/fs"
	"log"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/Bartis-Dev/libs/go/d1"

	_ "modernc.org/sqlite" // Der CGO-freie SQLite Treiber!
)

//go:embed all:frontend/out
var staticFiles embed.FS

// --- DATENMODELLE ---

type Location struct {
	Lat float64 `json:"lat"`
	Lng float64 `json:"lng"`
}

type User struct {
	ID         string              `json:"id"`
	Name       string              `json:"name"`
	FoundTerms map[string]Location `json:"foundTerms"`
}

type VoteCount struct {
	Approve int
	Reject  int
}

type RoomState struct {
	Code               string                     `json:"code"`
	Terms              []string                   `json:"terms"`
	Users              map[string]*User           `json:"users"`
	TimeLimit          int                        `json:"timeLimit"`
	StartTime          string                     `json:"startTime"`
	Finished           bool                       `json:"finished"`
	FinishedBy         string                     `json:"finishedBy"`
	Started            bool                       `json:"started"`
	CreatorId          string                     `json:"creatorId"`
	ResultsTermIdx     int                        `json:"resultsTermIdx"`
	ResultsPlayerIdx   int                        `json:"resultsPlayerIdx"`
	ResultsPlayerOrder []string                   `json:"resultsPlayerOrder"`
	ResultsVotes       map[string]bool            `json:"resultsVotes"`
	ResultsApproved    map[string]map[string]bool `json:"resultsApproved"`
	ResultsCountdown   bool                       `json:"resultsCountdown"`
	EvalTimeLeft       int                        `json:"evalTimeLeft"`
}

// In-Memory State für Auswertungs-Navigation
type ResultsNav struct {
	TermIndex     int
	PlayerIndex   int
	PlayerOrder   []string
	Votes         map[string]bool                  // voterId -> approve/reject (aktueller Schritt)
	VoteResults   map[string]map[string]*VoteCount // term -> userId -> {approve, reject}
	Countdown     bool
	StepStartTime time.Time
}

var (
	resultsNav   = make(map[string]*ResultsNav)
	resultsNavMu sync.Mutex
	evalTimers   = make(map[string]*time.Timer)
	evalTimersMu sync.Mutex
)

var (
	db         *sql.DB
	sseClients = make(map[string]map[chan string]bool)
	sseMu      sync.RWMutex
)

// --- HILFSFUNKTIONEN ---

func generateCode(length int) string {
	b := make([]byte, length)
	rand.Read(b)
	return fmt.Sprintf("%X", b)
}

func loadEnv() {
	data, err := os.ReadFile(".env")
	if err != nil {
		log.Println("Keine .env Datei gefunden. Nutze System-Umgebungsvariablen.")
		return
	}
	lines := strings.Split(string(data), "\n")
	for _, line := range lines {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		parts := strings.SplitN(line, "=", 2)
		if len(parts) == 2 {
			os.Setenv(strings.TrimSpace(parts[0]), strings.TrimSpace(parts[1]))
		}
	}
}

// --- DATENBANK & BEREINIGUNG ---

func initDB() {
	var err error
	// With GEOBINGO_D1_URL set, rooms live in Cloudflare D1 (via data-proxy),
	// so the container keeps no state on disk and can restart on any host.
	if url := os.Getenv("GEOBINGO_D1_URL"); url != "" {
		token := os.Getenv("GEOBINGO_D1_TOKEN")
		if token == "" {
			log.Fatal("GEOBINGO_D1_TOKEN is required when GEOBINGO_D1_URL is set")
		}
		db = sql.OpenDB(d1.NewConnector(url, token))
		log.Println("Datenbank: Cloudflare D1")
	} else {
		db, err = sql.Open("sqlite", "./geobingo.db")
		if err != nil {
			log.Fatal("Konnte Datenbank nicht öffnen:", err)
		}
		// Performance Optimierung für SQLite bei gleichzeitigen Zugriffen
		db.Exec("PRAGMA journal_mode=WAL;")
	}

	// Wait out a D1/network outage in-process instead of crash-looping;
	// permanent errors (wrong token, bad SQL) cannot heal, so exit at once.
	delay := time.Second
	for attempt := 1; ; attempt++ {
		err = createTables()
		if err == nil {
			break
		}
		if !d1.IsTransient(err) {
			log.Fatal("Konnte Datenbank nicht vorbereiten (kein Retry): ", err)
		}
		log.Printf("Datenbank nicht erreichbar (Versuch %d), neuer Versuch in %s: %v", attempt, delay, err)
		time.Sleep(delay)
		delay = min(delay*2, 30*time.Second)
	}
	// Migration: Spalten hinzufügen falls sie fehlen (für bestehende DBs)
	db.Exec("ALTER TABLE rooms ADD COLUMN finished INTEGER DEFAULT 0")
	db.Exec("ALTER TABLE rooms ADD COLUMN finishedBy TEXT DEFAULT ''")
	db.Exec("ALTER TABLE rooms ADD COLUMN started INTEGER DEFAULT 0")
	db.Exec("ALTER TABLE rooms ADD COLUMN creatorId TEXT DEFAULT ''")
}

func createTables() error {
	if err := db.Ping(); err != nil {
		return fmt.Errorf("ping: %w", err)
	}
	// One statement per Exec: D1 runs only a single statement per call.
	tables := []string{
		`CREATE TABLE IF NOT EXISTS rooms (code TEXT PRIMARY KEY, terms TEXT, timeLimit INTEGER, startTime INTEGER, expiresAt INTEGER, finished INTEGER DEFAULT 0, finishedBy TEXT DEFAULT '', started INTEGER DEFAULT 0, creatorId TEXT DEFAULT '')`,
		`CREATE TABLE IF NOT EXISTS users (id TEXT PRIMARY KEY, name TEXT)`,
		`CREATE TABLE IF NOT EXISTS room_users (roomCode TEXT, userId TEXT, PRIMARY KEY(roomCode, userId))`,
		`CREATE TABLE IF NOT EXISTS found_terms (roomCode TEXT, userId TEXT, term TEXT, lat REAL, lng REAL, PRIMARY KEY(roomCode, userId, term))`,
	}
	for _, t := range tables {
		if _, err := db.Exec(t); err != nil {
			return fmt.Errorf("Konnte Tabellen nicht anlegen: %w", err)
		}
	}
	return nil
}

// Löscht regelmäßig Räume, die abgelaufen sind
func startCleanupTask() {
	go func() {
		for {
			time.Sleep(5 * time.Minute)
			now := time.Now().Unix()

			// Finde abgelaufene Räume
			rows, err := db.Query("SELECT code FROM rooms WHERE expiresAt < ?", now)
			if err == nil {
				var expiredCodes []string
				for rows.Next() {
					var code string
					rows.Scan(&code)
					expiredCodes = append(expiredCodes, code)
				}
				rows.Close()

				// Lösche sie mitsamt ihren Verknüpfungen
				for _, code := range expiredCodes {
					db.Exec("DELETE FROM rooms WHERE code = ?", code)
					db.Exec("DELETE FROM room_users WHERE roomCode = ?", code)
					db.Exec("DELETE FROM found_terms WHERE roomCode = ?", code)

					// Schließe Verbindungen
					sseMu.Lock()
					delete(sseClients, code)
					sseMu.Unlock()

					// Bereinige auch die Results-Navigation
					resultsNavMu.Lock()
					delete(resultsNav, code)
					resultsNavMu.Unlock()

					log.Printf("🗑️ Raum %s wurde automatisch gelöscht (Abgelaufen)", code)
				}
			}
		}
	}()
}

// Baut den aktuellen Raum-Status komplett aus der SQLite zusammen
func fetchRoomState(code string) (*RoomState, error) {
	var termsStr string
	var timeLimit, startTimeUnix int64
	var finished, started int
	var finishedBy, creatorId string

	err := db.QueryRow("SELECT terms, timeLimit, startTime, finished, finishedBy, started, creatorId FROM rooms WHERE code = ?", code).Scan(&termsStr, &timeLimit, &startTimeUnix, &finished, &finishedBy, &started, &creatorId)
	if err != nil {
		return nil, err
	}

	var terms []string
	json.Unmarshal([]byte(termsStr), &terms)

	state := &RoomState{
		Code:       code,
		Terms:      terms,
		TimeLimit:  int(timeLimit),
		StartTime:  time.Unix(startTimeUnix, 0).Format(time.RFC3339),
		Finished:   finished == 1,
		FinishedBy: finishedBy,
		Started:    started == 1,
		CreatorId:  creatorId,
		Users:      make(map[string]*User),
	}

	// Results-Navigation aus In-Memory lesen
	resultsNavMu.Lock()
	if nav, ok := resultsNav[code]; ok {
		state.ResultsTermIdx = nav.TermIndex
		state.ResultsPlayerIdx = nav.PlayerIndex
		state.ResultsPlayerOrder = nav.PlayerOrder
		state.ResultsVotes = nav.Votes
		state.ResultsCountdown = nav.Countdown
		state.EvalTimeLeft = 90 - int(time.Since(nav.StepStartTime).Seconds())
		if state.EvalTimeLeft < 0 {
			state.EvalTimeLeft = 0
		}

		// ResultsApproved berechnen: für jedes (term, userId) prüfe ob approve > reject
		state.ResultsApproved = make(map[string]map[string]bool)
		for term, userVotes := range nav.VoteResults {
			state.ResultsApproved[term] = make(map[string]bool)
			for uid, vc := range userVotes {
				state.ResultsApproved[term][uid] = vc.Approve > vc.Reject
			}
		}
	}
	resultsNavMu.Unlock()

	// 1. Hole alle Nutzer in diesem Raum
	uRows, err := db.Query("SELECT u.id, u.name FROM users u JOIN room_users ru ON u.id = ru.userId WHERE ru.roomCode = ?", code)
	if err == nil {
		defer uRows.Close()
		for uRows.Next() {
			var uid, name string
			uRows.Scan(&uid, &name)
			state.Users[uid] = &User{ID: uid, Name: name, FoundTerms: make(map[string]Location)}
		}
	}

	// 2. Hole alle gefundenen Begriffe in diesem Raum
	fRows, err := db.Query("SELECT userId, term, lat, lng FROM found_terms WHERE roomCode = ?", code)
	if err == nil {
		defer fRows.Close()
		for fRows.Next() {
			var uid, term string
			var lat, lng float64
			fRows.Scan(&uid, &term, &lat, &lng)
			if u, exists := state.Users[uid]; exists {
				u.FoundTerms[term] = Location{Lat: lat, Lng: lng}
			}
		}
	}

	return state, nil
}

func broadcastRoomUpdate(code string) {
	state, err := fetchRoomState(code)
	if err != nil {
		return
	}
	data, _ := json.Marshal(state)
	msg := string(data)

	sseMu.RLock()
	clients := sseClients[code]
	sseMu.RUnlock()

	for client := range clients {
		select {
		case client <- msg:
		default:
		}
	}
}

// --- API HANDLER ---

// requireSession returns the session user id from the cookie, or writes a 401
// and returns ok=false. This avoids the nil-pointer panic that happened when a
// handler did `cookie, _ := r.Cookie(...)` and then used cookie.Value while the
// cookie was missing.
func requireSession(w http.ResponseWriter, r *http.Request) (string, bool) {
	cookie, err := r.Cookie("session_id")
	if err != nil || cookie.Value == "" {
		http.Error(w, "Nicht angemeldet", http.StatusUnauthorized)
		return "", false
	}
	return cookie.Value, true
}

// isHTTPS reports whether the request reached us over TLS, either directly or
// through a reverse proxy that set X-Forwarded-Proto. Used to mark the session
// cookie Secure only when it actually travels over HTTPS.
func isHTTPS(r *http.Request) bool {
	if r.TLS != nil {
		return true
	}
	return strings.EqualFold(r.Header.Get("X-Forwarded-Proto"), "https")
}

// generateUniqueRoomCode returns a room code that is not already in use.
func generateUniqueRoomCode() string {
	for i := 0; i < 10; i++ {
		code := generateCode(3)
		var dummy string
		err := db.QueryRow("SELECT code FROM rooms WHERE code = ?", code).Scan(&dummy)
		if err == sql.ErrNoRows {
			return code
		}
	}
	// Extremely unlikely fallback: a longer code.
	return generateCode(4)
}

func handleHealth(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	w.Write([]byte(`{"status":"ok"}`))
}

func handleConfig(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]string{"mapsApiKey": os.Getenv("MAPS_API_KEY")})
}

func handleSession(w http.ResponseWriter, r *http.Request) {
	cookie, err := r.Cookie("session_id")
	if err != nil {
		http.Error(w, "Unauthorized", http.StatusUnauthorized)
		return
	}
	var name string
	err = db.QueryRow("SELECT name FROM users WHERE id = ?", cookie.Value).Scan(&name)
	if err != nil {
		http.Error(w, "Unauthorized", http.StatusUnauthorized)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]string{"id": cookie.Value, "name": name})
}

func handleLogin(w http.ResponseWriter, r *http.Request) {
	var req struct{ Name string }
	json.NewDecoder(r.Body).Decode(&req)

	name := strings.TrimSpace(req.Name)
	if name == "" {
		http.Error(w, "Name darf nicht leer sein", http.StatusBadRequest)
		return
	}
	if runes := []rune(name); len(runes) > 30 {
		name = string(runes[:30])
	}

	uid := generateCode(8)
	db.Exec("INSERT INTO users (id, name) VALUES (?, ?)", uid, name)

	http.SetCookie(w, &http.Cookie{
		Name:     "session_id",
		Value:    uid,
		Path:     "/",
		HttpOnly: true,
		SameSite: http.SameSiteLaxMode,
		Secure:   isHTTPS(r),
		MaxAge:   60 * 60 * 24 * 30, // 30 Tage
	})
	json.NewEncoder(w).Encode(map[string]string{"id": uid, "name": name})
}

func handleCreateRoom(w http.ResponseWriter, r *http.Request) {
	uid, ok := requireSession(w, r)
	if !ok {
		return
	}
	var req struct {
		Terms     []string `json:"terms"`
		TimeLimit int      `json:"timeLimit"`
	}
	json.NewDecoder(r.Body).Decode(&req)

	// Begriffe serverseitig säubern: trimmen und leere verwerfen.
	terms := make([]string, 0, len(req.Terms))
	for _, t := range req.Terms {
		if t = strings.TrimSpace(t); t != "" {
			terms = append(terms, t)
		}
	}

	if len(terms) == 0 {
		http.Error(w, "Mindestens ein Begriff erforderlich", http.StatusBadRequest)
		return
	}
	if len(terms) > 20 {
		http.Error(w, "Maximal 20 Begriffe erlaubt", http.StatusBadRequest)
		return
	}
	totalLen := 0
	for _, t := range terms {
		totalLen += len(t)
	}
	if totalLen > 500 {
		http.Error(w, "Maximal 500 Zeichen erlaubt", http.StatusBadRequest)
		return
	}

	// Doppelte Begriffe prüfen (case-insensitive)
	seen := make(map[string]bool)
	for _, t := range terms {
		lower := strings.ToLower(t)
		if seen[lower] {
			http.Error(w, "Doppelte Begriffe sind nicht erlaubt", http.StatusBadRequest)
			return
		}
		seen[lower] = true
	}

	if req.TimeLimit < 0 {
		req.TimeLimit = 0
	}

	code := generateUniqueRoomCode()
	termsJSON, _ := json.Marshal(terms)

	now := time.Now()
	// Dauer + 180 Minuten Nachlaufzeit (genug Zeit für Pausen und langes Voting)
	expiresAt := now.Add(time.Duration(req.TimeLimit+180) * time.Minute).Unix()

	db.Exec("INSERT INTO rooms (code, terms, timeLimit, startTime, expiresAt, started, creatorId) VALUES (?, ?, ?, 0, ?, 0, ?)",
		code, string(termsJSON), req.TimeLimit, expiresAt, uid)

	// Creator automatisch dem Raum hinzufügen
	db.Exec("INSERT OR IGNORE INTO room_users (roomCode, userId) VALUES (?, ?)", code, uid)

	json.NewEncoder(w).Encode(map[string]string{"code": code})
}

func handleJoinRoom(w http.ResponseWriter, r *http.Request) {
	uid, ok := requireSession(w, r)
	if !ok {
		return
	}
	var req struct{ Code string }
	json.NewDecoder(r.Body).Decode(&req)
	req.Code = strings.ToUpper(strings.TrimSpace(req.Code))

	// Prüfe ob Raum existiert (und nicht abgelaufen ist)
	var dummy string
	err := db.QueryRow("SELECT code FROM rooms WHERE code = ? AND expiresAt > ?", req.Code, time.Now().Unix()).Scan(&dummy)
	if err != nil {
		http.Error(w, "Raum nicht gefunden", http.StatusNotFound)
		return
	}

	// Füge Nutzer zum Raum hinzu (Ignoriere Fehler, wenn er schon drin ist - dank PRIMARY KEY)
	db.Exec("INSERT OR IGNORE INTO room_users (roomCode, userId) VALUES (?, ?)", req.Code, uid)

	broadcastRoomUpdate(req.Code)
	json.NewEncoder(w).Encode(map[string]string{"status": "joined", "code": req.Code})
}

func startRoundTimer(code string, timeLimit int) {
	go func() {
		time.Sleep(time.Duration(timeLimit) * time.Minute)

		var finished int
		err := db.QueryRow("SELECT finished FROM rooms WHERE code = ?", code).Scan(&finished)
		if err != nil || finished == 1 {
			return
		}

		db.Exec("UPDATE rooms SET finished = 1, finishedBy = 'Timer' WHERE code = ? AND finished = 0", code)

		var playerOrder []string
		orderRows, _ := db.Query("SELECT userId FROM room_users WHERE roomCode = ? ORDER BY rowid", code)
		if orderRows != nil {
			for orderRows.Next() {
				var uid string
				orderRows.Scan(&uid)
				playerOrder = append(playerOrder, uid)
			}
			orderRows.Close()
		}

		resultsNavMu.Lock()
		nav := &ResultsNav{
			TermIndex:     0,
			PlayerIndex:   0,
			PlayerOrder:   playerOrder,
			Votes:         make(map[string]bool),
			VoteResults:   make(map[string]map[string]*VoteCount),
			StepStartTime: time.Now(),
		}
		resultsNav[code] = nav
		resultsNavMu.Unlock()

		state, err := fetchRoomState(code)
		if err == nil {
			resultsNavMu.Lock()
			autoSkipIfNotFound(code, nav, state)
			resultsNavMu.Unlock()
		}

		broadcastRoomUpdate(code)
		startEvalTimer(code)
	}()
}

func handleStartRoom(w http.ResponseWriter, r *http.Request) {
	uid, ok := requireSession(w, r)
	if !ok {
		return
	}
	var req struct {
		RoomCode string `json:"roomCode"`
	}
	json.NewDecoder(r.Body).Decode(&req)

	var creatorId string
	var started int
	err := db.QueryRow("SELECT creatorId, started FROM rooms WHERE code = ?", req.RoomCode).Scan(&creatorId, &started)
	if err != nil {
		http.Error(w, "Raum nicht gefunden", http.StatusNotFound)
		return
	}
	if uid != creatorId {
		http.Error(w, "Nur der Ersteller kann starten", http.StatusForbidden)
		return
	}
	if started == 1 {
		http.Error(w, "Bereits gestartet", http.StatusBadRequest)
		return
	}

	now := time.Now()
	db.Exec("UPDATE rooms SET started = 1, startTime = ? WHERE code = ?", now.Unix(), req.RoomCode)

	// Round-Timer starten
	var timeLimit int
	db.QueryRow("SELECT timeLimit FROM rooms WHERE code = ?", req.RoomCode).Scan(&timeLimit)
	if timeLimit > 0 {
		startRoundTimer(req.RoomCode, timeLimit)
	}

	broadcastRoomUpdate(req.RoomCode)
	w.WriteHeader(http.StatusOK)
}

func handleMarkTerm(w http.ResponseWriter, r *http.Request) {
	uid, ok := requireSession(w, r)
	if !ok {
		return
	}
	var req struct {
		RoomCode string   `json:"roomCode"`
		Term     string   `json:"term"`
		Location Location `json:"location"`
	}
	json.NewDecoder(r.Body).Decode(&req)

	var started int
	var termsStr string
	err := db.QueryRow("SELECT started, terms FROM rooms WHERE code = ?", req.RoomCode).Scan(&started, &termsStr)
	if err != nil || started == 0 {
		http.Error(w, "Spiel noch nicht gestartet", http.StatusBadRequest)
		return
	}

	// Nur Begriffe akzeptieren, die wirklich zum Raum gehören.
	var terms []string
	json.Unmarshal([]byte(termsStr), &terms)
	valid := false
	for _, t := range terms {
		if t == req.Term {
			valid = true
			break
		}
	}
	if !valid {
		http.Error(w, "Unbekannter Begriff", http.StatusBadRequest)
		return
	}

	db.Exec("INSERT OR REPLACE INTO found_terms (roomCode, userId, term, lat, lng) VALUES (?, ?, ?, ?, ?)",
		req.RoomCode, uid, req.Term, req.Location.Lat, req.Location.Lng)

	broadcastRoomUpdate(req.RoomCode)
	w.WriteHeader(http.StatusOK)
}

func handleUnmarkTerm(w http.ResponseWriter, r *http.Request) {
	uid, ok := requireSession(w, r)
	if !ok {
		return
	}
	var req struct {
		RoomCode string `json:"roomCode"`
		Term     string `json:"term"`
	}
	json.NewDecoder(r.Body).Decode(&req)

	var finished int
	err := db.QueryRow("SELECT finished FROM rooms WHERE code = ?", req.RoomCode).Scan(&finished)
	if err != nil || finished == 1 {
		http.Error(w, "Raum nicht gefunden oder bereits beendet", http.StatusBadRequest)
		return
	}

	db.Exec("DELETE FROM found_terms WHERE roomCode = ? AND userId = ? AND term = ?",
		req.RoomCode, uid, req.Term)

	broadcastRoomUpdate(req.RoomCode)
	w.WriteHeader(http.StatusOK)
}

func handleFinishRoom(w http.ResponseWriter, r *http.Request) {
	uid, ok := requireSession(w, r)
	if !ok {
		return
	}
	var req struct {
		RoomCode string `json:"roomCode"`
	}
	json.NewDecoder(r.Body).Decode(&req)

	// Prüfe ob der User wirklich alle Begriffe gefunden hat
	state, err := fetchRoomState(req.RoomCode)
	if err != nil {
		http.Error(w, "Raum nicht gefunden", http.StatusNotFound)
		return
	}
	if state.Finished {
		http.Error(w, "Runde bereits beendet", http.StatusBadRequest)
		return
	}
	user, exists := state.Users[uid]
	if !exists {
		http.Error(w, "User nicht im Raum", http.StatusForbidden)
		return
	}
	uniqueTerms := make(map[string]bool)
	for _, t := range state.Terms {
		uniqueTerms[t] = true
	}
	if len(user.FoundTerms) < len(uniqueTerms) {
		http.Error(w, "Noch nicht alle Begriffe gefunden", http.StatusBadRequest)
		return
	}

	db.Exec("UPDATE rooms SET finished = 1, finishedBy = ? WHERE code = ?", user.Name, req.RoomCode)

	// Results-Navigation initialisieren mit stabiler Spieler-Reihenfolge
	var playerOrder []string
	orderRows, _ := db.Query("SELECT userId FROM room_users WHERE roomCode = ? ORDER BY rowid", req.RoomCode)
	if orderRows != nil {
		for orderRows.Next() {
			var uid string
			orderRows.Scan(&uid)
			playerOrder = append(playerOrder, uid)
		}
		orderRows.Close()
	}

	resultsNavMu.Lock()
	nav := &ResultsNav{
		TermIndex:     0,
		PlayerIndex:   0,
		PlayerOrder:   playerOrder,
		Votes:         make(map[string]bool),
		VoteResults:   make(map[string]map[string]*VoteCount),
		StepStartTime: time.Now(),
	}
	resultsNav[req.RoomCode] = nav
	resultsNavMu.Unlock()

	// Auto-Skip falls erster Spieler den ersten Begriff nicht gefunden hat
	go func() {
		resultsNavMu.Lock()
		autoSkipIfNotFound(req.RoomCode, nav, state)
		resultsNavMu.Unlock()
		broadcastRoomUpdate(req.RoomCode)
		startEvalTimer(req.RoomCode)
	}()

	broadcastRoomUpdate(req.RoomCode)
	w.WriteHeader(http.StatusOK)
}

func startEvalTimer(roomCode string) {
	evalTimersMu.Lock()
	if t, ok := evalTimers[roomCode]; ok {
		t.Stop()
	}
	evalTimers[roomCode] = time.AfterFunc(90*time.Second, func() {
		autoAdvanceEval(roomCode)
	})
	evalTimersMu.Unlock()
}

func stopEvalTimer(roomCode string) {
	evalTimersMu.Lock()
	if t, ok := evalTimers[roomCode]; ok {
		t.Stop()
		delete(evalTimers, roomCode)
	}
	evalTimersMu.Unlock()
}

func autoAdvanceEval(roomCode string) {
	state, err := fetchRoomState(roomCode)
	if err != nil {
		return
	}

	resultsNavMu.Lock()
	nav, ok := resultsNav[roomCode]
	if !ok || nav.TermIndex >= len(state.Terms) {
		resultsNavMu.Unlock()
		return
	}

	currentTerm := state.Terms[nav.TermIndex]
	currentPlayerID := nav.PlayerOrder[nav.PlayerIndex]
	if nav.VoteResults[currentTerm] == nil {
		nav.VoteResults[currentTerm] = make(map[string]*VoteCount)
	}
	vc := &VoteCount{}
	for _, approve := range nav.Votes {
		if approve {
			vc.Approve++
		} else {
			vc.Reject++
		}
	}
	nav.VoteResults[currentTerm][currentPlayerID] = vc
	nav.Countdown = true
	resultsNavMu.Unlock()

	broadcastRoomUpdate(roomCode)

	time.Sleep(5 * time.Second)

	st, err := fetchRoomState(roomCode)
	resultsNavMu.Lock()
	nav, ok = resultsNav[roomCode]
	if ok && err == nil {
		advanceToNext(roomCode, nav, st)
		nav.StepStartTime = time.Now()
	}
	stillGoing := ok && nav.TermIndex < len(st.Terms)
	resultsNavMu.Unlock()

	broadcastRoomUpdate(roomCode)

	if stillGoing {
		startEvalTimer(roomCode)
	}
}

// autoSkipIfNotFound prüft ob der aktuelle Spieler den aktuellen Begriff gefunden hat.
// Falls nicht, springt es automatisch zum nächsten Spieler/Begriff.
// MUSS mit resultsNavMu gelockt aufgerufen werden.
func autoSkipIfNotFound(roomCode string, nav *ResultsNav, state *RoomState) {
	for {
		if nav.TermIndex >= len(state.Terms) {
			return // Alle Begriffe durch
		}
		currentTerm := state.Terms[nav.TermIndex]
		if nav.PlayerIndex >= len(nav.PlayerOrder) {
			nav.PlayerIndex = 0
			nav.TermIndex++
			nav.Votes = make(map[string]bool)
			continue
		}
		currentPlayerID := nav.PlayerOrder[nav.PlayerIndex]
		currentUser, exists := state.Users[currentPlayerID]
		if !exists {
			nav.PlayerIndex++
			continue
		}
		_, found := currentUser.FoundTerms[currentTerm]
		if found {
			return // Spieler hat den Begriff → Voting nötig
		}
		// Nicht gefunden → überspringen
		nav.PlayerIndex++
		if nav.PlayerIndex >= len(nav.PlayerOrder) {
			nav.PlayerIndex = 0
			nav.TermIndex++
			nav.Votes = make(map[string]bool)
		}
	}
}

func advanceToNext(roomCode string, nav *ResultsNav, state *RoomState) {
	nav.PlayerIndex++
	nav.Votes = make(map[string]bool)
	nav.Countdown = false
	if nav.PlayerIndex >= len(nav.PlayerOrder) {
		nav.PlayerIndex = 0
		nav.TermIndex++
	}
	autoSkipIfNotFound(roomCode, nav, state)
}

func handleResultsVote(w http.ResponseWriter, r *http.Request) {
	uid, authed := requireSession(w, r)
	if !authed {
		return
	}
	var req struct {
		RoomCode string `json:"roomCode"`
		Approve  bool   `json:"approve"`
	}
	json.NewDecoder(r.Body).Decode(&req)

	state, err := fetchRoomState(req.RoomCode)
	if err != nil || !state.Finished {
		http.Error(w, "Raum nicht gefunden oder nicht beendet", http.StatusBadRequest)
		return
	}

	resultsNavMu.Lock()
	nav, ok := resultsNav[req.RoomCode]
	if !ok {
		resultsNavMu.Unlock()
		http.Error(w, "Keine Auswertung aktiv", http.StatusBadRequest)
		return
	}

	// Prüfe ob Auswertung schon vorbei ist
	if nav.TermIndex >= len(state.Terms) {
		resultsNavMu.Unlock()
		http.Error(w, "Auswertung bereits beendet", http.StatusBadRequest)
		return
	}

	// Aktueller Spieler darf nicht für sich selbst voten
	if nav.PlayerIndex >= len(nav.PlayerOrder) {
		resultsNavMu.Unlock()
		http.Error(w, "Auswertung nicht aktiv", http.StatusBadRequest)
		return
	}
	currentPlayerID := nav.PlayerOrder[nav.PlayerIndex]
	if uid == currentPlayerID {
		resultsNavMu.Unlock()
		http.Error(w, "Du kannst nicht für dich selbst voten", http.StatusForbidden)
		return
	}

	// Prüfe ob User schon gevotet hat
	if _, already := nav.Votes[uid]; already {
		resultsNavMu.Unlock()
		http.Error(w, "Bereits gevotet", http.StatusBadRequest)
		return
	}

	// Vote speichern
	nav.Votes[uid] = req.Approve

	// Prüfe ob alle berechtigten Voter gevotet haben (alle außer aktueller Spieler)
	eligibleVoters := len(state.Users) - 1
	allVoted := len(nav.Votes) >= eligibleVoters

	if allVoted {
		// Vote-Ergebnis akkumulieren
		currentTerm := state.Terms[nav.TermIndex]
		if nav.VoteResults[currentTerm] == nil {
			nav.VoteResults[currentTerm] = make(map[string]*VoteCount)
		}
		vc := &VoteCount{}
		for _, approve := range nav.Votes {
			if approve {
				vc.Approve++
			} else {
				vc.Reject++
			}
		}
		nav.VoteResults[currentTerm][currentPlayerID] = vc
		nav.Countdown = true
	}
	resultsNavMu.Unlock()

	broadcastRoomUpdate(req.RoomCode)

	if allVoted {
		stopEvalTimer(req.RoomCode)
		// 5s Countdown, dann weiter
		go func() {
			time.Sleep(5 * time.Second)

			st, err := fetchRoomState(req.RoomCode)

			resultsNavMu.Lock()
			nav, ok := resultsNav[req.RoomCode]
			if ok && err == nil {
				advanceToNext(req.RoomCode, nav, st)
				nav.StepStartTime = time.Now()
			}
			stillGoing := ok && nav.TermIndex < len(st.Terms)
			resultsNavMu.Unlock()

			broadcastRoomUpdate(req.RoomCode)

			if stillGoing {
				startEvalTimer(req.RoomCode)
			}
		}()
	}

	w.WriteHeader(http.StatusOK)
}

func handleRoomStream(w http.ResponseWriter, r *http.Request) {
	roomCode := r.URL.Query().Get("code")

	// Initiales Senden
	state, err := fetchRoomState(roomCode)
	if err != nil {
		http.Error(w, "Raum nicht gefunden", http.StatusNotFound)
		return
	}

	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "Streaming unsupported", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")

	clientChan := make(chan string)

	sseMu.Lock()
	if sseClients[roomCode] == nil {
		sseClients[roomCode] = make(map[chan string]bool)
	}
	sseClients[roomCode][clientChan] = true
	sseMu.Unlock()

	// Sofort den aktuellen Status senden
	initData, _ := json.Marshal(state)
	fmt.Fprintf(w, "data: %s\n\n", string(initData))
	flusher.Flush()

	defer func() {
		sseMu.Lock()
		delete(sseClients[roomCode], clientChan)
		sseMu.Unlock()
	}()

	notify := r.Context().Done()
	for {
		select {
		case msg := <-clientChan:
			fmt.Fprintf(w, "data: %s\n\n", msg)
			flusher.Flush()
		case <-notify:
			return
		}
	}
}

// --- MAIN ---

func main() {
	loadEnv()
	initDB()
	startCleanupTask()

	mux := http.NewServeMux()

	mux.HandleFunc("/api/health", handleHealth)
	mux.HandleFunc("/api/config", handleConfig)
	mux.HandleFunc("/api/session", handleSession)
	mux.HandleFunc("/api/login", handleLogin)
	mux.HandleFunc("/api/room/create", handleCreateRoom)
	mux.HandleFunc("/api/room/join", handleJoinRoom)
	mux.HandleFunc("/api/room/start", handleStartRoom)
	mux.HandleFunc("/api/room/mark", handleMarkTerm)
	mux.HandleFunc("/api/room/unmark", handleUnmarkTerm)
	mux.HandleFunc("/api/room/finish", handleFinishRoom)
	mux.HandleFunc("/api/room/results/vote", handleResultsVote)
	mux.HandleFunc("/api/room/stream", handleRoomStream)

	// Fallback-Routing für die React Single Page App
	subFS, _ := fs.Sub(staticFiles, "frontend/out")
	fileServer := http.FileServer(http.FS(subFS))

	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		path := r.URL.Path
		if path == "/" {
			path = "index.html"
		} else {
			path = strings.TrimPrefix(path, "/")
		}

		// Wenn die Datei (wie z.B. /_next/...) nicht existiert, leite auf index.html um (SPA Verhalten)
		_, err := fs.Stat(subFS, path)
		if os.IsNotExist(err) {
			r.URL.Path = "/"
		}
		fileServer.ServeHTTP(w, r)
	})

	port := os.Getenv("PORT")
	if port == "" {
		port = "10200"
	}

	log.Printf("🚀 GeoBingo läuft auf http://localhost:%s", port)
	log.Fatal(http.ListenAndServe(":"+port, mux))
}
