'use client';

import { useState, useEffect, useRef, FormEvent } from 'react';
import { useToast } from './toast';

// Globale Typen für Google Maps
declare global {
  interface Window {
    google: any;
    gm_authFailure: () => void;
  }
}

type Location = { lat: number; lng: number };
type User = { id: string; name: string; foundTerms: Record<string, Location> };
type RoomState = {
  code: string; terms: string[]; users: Record<string, User>; timeLimit: number; startTime: string;
  finished: boolean; finishedBy: string; started: boolean; creatorId: string;
  resultsTermIdx: number; resultsPlayerIdx: number; resultsPlayerOrder: string[];
  resultsVotes: Record<string, boolean>; resultsApproved: Record<string, Record<string, boolean>>;
  resultsCountdown: boolean; evalTimeLeft: number;
};

// Dunkler Kartenstil, damit die Übersichtskarte zum Theme passt.
const DARK_MAP_STYLE = [
  { elementType: 'geometry', stylers: [{ color: '#0f141b' }] },
  { elementType: 'labels.text.stroke', stylers: [{ color: '#0a0e13' }] },
  { elementType: 'labels.text.fill', stylers: [{ color: '#8b98a8' }] },
  { featureType: 'administrative', elementType: 'geometry', stylers: [{ color: '#2a3543' }] },
  { featureType: 'poi', elementType: 'geometry', stylers: [{ color: '#14202a' }] },
  { featureType: 'poi', elementType: 'labels.text.fill', stylers: [{ color: '#6b7a89' }] },
  { featureType: 'poi.park', elementType: 'geometry', stylers: [{ color: '#102a20' }] },
  { featureType: 'road', elementType: 'geometry', stylers: [{ color: '#202b36' }] },
  { featureType: 'road', elementType: 'geometry.stroke', stylers: [{ color: '#11161d' }] },
  { featureType: 'road', elementType: 'labels.text.fill', stylers: [{ color: '#9aa7b6' }] },
  { featureType: 'transit', stylers: [{ visibility: 'off' }] },
  { featureType: 'water', elementType: 'geometry', stylers: [{ color: '#0a1620' }] },
];

// --- Icons (inline SVG, currentColor) ---
type IconProps = { className?: string };
const svg = (className = 'w-4 h-4') => ({
  width: 16, height: 16, viewBox: '0 0 24 24', fill: 'none',
  stroke: 'currentColor', strokeWidth: 1.8, strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const, className,
});
const PinIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M12 21s-7-6-7-11a7 7 0 1 1 14 0c0 5-7 11-7 11Z" /><circle cx="12" cy="10" r="2.5" /></svg>);
const LinkIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M10 13a5 5 0 0 0 7 0l2-2a5 5 0 0 0-7-7l-1 1" /><path d="M14 11a5 5 0 0 0-7 0l-2 2a5 5 0 0 0 7 7l1-1" /></svg>);
const TrashIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m1 0v12a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V7" /></svg>);
const EditIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M12 20h9" /><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" /></svg>);
const UsersIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M16 19v-1a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v1" /><circle cx="9" cy="7" r="3.2" /><path d="M22 19v-1a4 4 0 0 0-3-3.8" /><path d="M16 4.2A4 4 0 0 1 16 11" /></svg>);
const ArrowIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M5 12h14M13 6l6 6-6 6" /></svg>);
const CheckIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M20 6 9 17l-5-5" /></svg>);
const CloseIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M18 6 6 18M6 6l12 12" /></svg>);
const FlagIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M5 21V4m0 0 5-1 4 2 5-1v9l-5 1-4-2-5 1" /></svg>);
const SearchIcon = ({ className }: IconProps) => (<svg {...svg(className)}><circle cx="11" cy="11" r="7" /><path d="m21 21-4.3-4.3" /></svg>);
const ChevronIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="m6 9 6 6 6-6" /></svg>);
const TrophyIcon = ({ className }: IconProps) => (<svg {...svg(className)}><path d="M8 21h8M12 17v4M7 4h10v4a5 5 0 0 1-10 0Z" /><path d="M17 5h3v1a3 3 0 0 1-3 3M7 5H4v1a3 3 0 0 0 3 3" /></svg>);

function BrandMark() {
  return (
    <div className="relative grid place-items-center w-14 h-14 rounded-2xl bg-elevated border border-line"
      style={{ boxShadow: '0 0 0 1px rgba(62,226,165,.15), 0 16px 40px -16px rgba(62,226,165,.45)' }}>
      <span className="absolute inset-0 rounded-2xl" style={{ background: 'radial-gradient(60% 60% at 50% 25%, rgba(62,226,165,.18), transparent)' }} />
      <PinIcon className="w-7 h-7 text-accent relative" />
    </div>
  );
}

export default function GeoBingoApp() {
  const toast = useToast();
  const [view, setView] = useState<'loading' | 'login' | 'lobby' | 'game'>('loading');

  const [username, setUsername] = useState('');
  const [roomCode, setRoomCode] = useState('');
  const [roomState, setRoomState] = useState<RoomState | null>(null);
  const [confirmOverwrite, setConfirmOverwrite] = useState<string | null>(null);
  const [timeRemaining, setTimeRemaining] = useState<number | null>(null);

  // Mobile-UI Zustände
  const [termsOpen, setTermsOpen] = useState(false);
  const [playersOpen, setPlayersOpen] = useState(false);

  const mapRef = useRef<HTMLDivElement>(null);
  const mapInstance = useRef<any>(null);
  const resultsMapRef = useRef<HTMLDivElement>(null);
  const resultsMapInstance = useRef<any>(null);

  // ==========================================
  // APP START & SESSION CHECK
  // ==========================================
  useEffect(() => {
    const init = async () => {
      try {
        const [res] = await Promise.all([
          fetch('/api/session'),
          loadGoogleMaps().catch(() => {})
        ]);
        if (res.ok) {
          const user = await res.json();
          setUsername(user.name);
          const pathCode = window.location.pathname.replace('/', '').toUpperCase();
          if (pathCode && pathCode.length > 0) {
            joinRoom(pathCode, true);
          } else {
            setView('lobby');
          }
        } else {
          setView('login');
        }
      } catch {
        setView('login');
      }
    };
    init();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // ==========================================
  // GOOGLE MAPS LADEN
  // ==========================================
  const loadGoogleMaps = async () => {
    if (window.google) return;

    const configRes = await fetch('/api/config');
    const config = await configRes.json();

    if (!config.mapsApiKey) {
      toast('error', 'Kein Google-Maps-API-Key konfiguriert. Bitte MAPS_API_KEY setzen.');
      return;
    }

    window.gm_authFailure = () => {
      toast('error', 'Google-Maps-Fehler — Billing & API-Aktivierung in der Cloud Console prüfen.');
    };

    await new Promise<void>((resolve, reject) => {
      const script = document.createElement('script');
      script.src = `https://maps.googleapis.com/maps/api/js?key=${config.mapsApiKey}&v=weekly`;
      script.async = true;
      script.onload = () => resolve();
      script.onerror = () => reject(new Error('Google Maps Script konnte nicht geladen werden'));
      document.head.appendChild(script);
    });
  };

  // ==========================================
  // PHASE 1: LOGIN
  // ==========================================
  const handleLogin = async (e: FormEvent) => {
    e.preventDefault();
    const name = username.trim();
    if (!name) return toast('warn', 'Bitte gib einen Namen ein.');

    try {
      await loadGoogleMaps();

      await fetch('/api/login', {
        method: 'POST',
        body: JSON.stringify({ name })
      });

      const pathCode = window.location.pathname.replace('/', '').toUpperCase();
      if (pathCode && pathCode.length > 0) {
        joinRoom(pathCode);
      } else {
        setView('lobby');
      }
    } catch (err) {
      console.error('Fehler beim Login:', err);
      toast('error', 'Keine Verbindung zum Server. Läuft das Backend?');
    }
  };

  // ==========================================
  // PHASE 2: LOBBY
  // ==========================================
  const [termsInput, setTermsInput] = useState('');
  const [termsError, setTermsError] = useState('');
  const [joinCode, setJoinCode] = useState('');
  const parsedTerms = termsInput.split(',').map(t => t.trim()).filter(t => t);

  // Live-Duplikat-Check
  useEffect(() => {
    const terms = termsInput.split(',').map(t => t.trim()).filter(t => t);
    const seen = new Set<string>();
    const dupes: string[] = [];
    for (const t of terms) {
      const lower = t.toLowerCase();
      if (seen.has(lower)) dupes.push(t);
      seen.add(lower);
    }
    setTermsError(dupes.length > 0 ? `Doppelte Begriffe: ${dupes.join(', ')}` : '');
  }, [termsInput]);

  const createRoom = async (e: FormEvent) => {
    e.preventDefault();
    const form = e.target as HTMLFormElement;
    const terms = parsedTerms;
    if (terms.length === 0) return toast('warn', 'Bitte mindestens einen Begriff eingeben.');
    if (terms.length > 20) return toast('warn', 'Maximal 20 Begriffe erlaubt.');
    if (termsInput.length > 500) return toast('warn', 'Maximal 500 Zeichen erlaubt.');
    if (termsError) return toast('warn', termsError);
    const timeLimit = parseInt((form.timeLimit as HTMLInputElement).value) || 0;

    try {
      const res = await fetch('/api/room/create', {
        method: 'POST',
        body: JSON.stringify({ terms, timeLimit })
      });
      if (!res.ok) {
        toast('error', (await res.text()) || 'Raum konnte nicht erstellt werden.');
        return;
      }
      const { code } = await res.json();
      joinRoom(code);
    } catch {
      toast('error', 'Verbindungsfehler beim Erstellen des Raums.');
    }
  };

  const joinRoom = async (code: string, silentFail = false) => {
    const clean = code.trim().toUpperCase();
    if (!clean) return;
    try {
      const res = await fetch('/api/room/join', {
        method: 'POST',
        body: JSON.stringify({ code: clean })
      });

      if (res.ok) {
        setRoomCode(clean);
        window.history.pushState({}, '', '/' + clean);
        setView('game');
      } else {
        if (!silentFail) toast('error', 'Raum nicht gefunden oder abgelaufen.');
        window.history.pushState({}, '', '/');
        setView('lobby');
      }
    } catch {
      if (!silentFail) toast('error', 'Verbindungsfehler beim Beitreten.');
      window.history.pushState({}, '', '/');
      setView('lobby');
    }
  };

  // ==========================================
  // PHASE 3: GAME & LIVE UPDATES
  // ==========================================

  // Effect 1: SSE-Verbindung mit Auto-Reconnect
  useEffect(() => {
    if (view !== 'game' || !roomCode) return;
    let cancelled = false;
    let eventSource: EventSource | null = null;

    const connect = () => {
      if (cancelled) return;
      eventSource = new EventSource(`/api/room/stream?code=${roomCode}`);
      eventSource.onmessage = (event) => {
        setRoomState(JSON.parse(event.data));
      };
      eventSource.onerror = () => {
        eventSource?.close();
        if (!cancelled) setTimeout(connect, 2000);
      };
    };
    connect();

    return () => {
      cancelled = true;
      eventSource?.close();
    };
  }, [view, roomCode]);

  // Effect: Timer-Countdown
  useEffect(() => {
    if (!roomState || roomState.finished || !roomState.timeLimit || !roomState.started) {
      setTimeRemaining(null);
      return;
    }
    const update = () => {
      const start = new Date(roomState.startTime).getTime();
      const end = start + roomState.timeLimit * 60 * 1000;
      const remaining = Math.max(0, Math.floor((end - Date.now()) / 1000));
      setTimeRemaining(remaining);
    };
    update();
    const interval = setInterval(update, 1000);
    return () => clearInterval(interval);
  }, [roomState?.startTime, roomState?.timeLimit, roomState?.finished, roomState?.started]);

  // Effect 2: Map-Initialisierung (wartet auf roomState → Map-Div existiert)
  const hasRoomState = !!roomState;
  useEffect(() => {
    if (view !== 'game' || !hasRoomState || mapInstance.current) return;

    let cancelled = false;
    const initMap = async () => {
      if (!window.google || !window.google.maps) {
        await loadGoogleMaps();
      }
      if (cancelled) return;
      if (mapRef.current && window.google?.maps) {
        const g = window.google.maps;
        mapInstance.current = new g.Map(mapRef.current, {
          center: { lat: 52.5162746, lng: 13.377704 },
          zoom: 14,
          streetViewControl: true,
          streetViewControlOptions: { position: g.ControlPosition.RIGHT_CENTER },
          zoomControl: true,
          zoomControlOptions: { position: g.ControlPosition.RIGHT_CENTER },
          mapTypeControl: false,
          fullscreenControl: false,
          backgroundColor: '#0f141b',
          styles: DARK_MAP_STYLE,
        });
      }
    };
    initMap();

    return () => {
      cancelled = true;
      mapInstance.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, hasRoomState]);

  // Aktionen im Spiel
  const markTermDirect = async (term: string) => {
    if (!mapInstance.current) return;
    const sv = mapInstance.current.getStreetView();
    if (!sv.getVisible()) {
      toast('warn', 'Du musst in der Street-View sein — ziehe das gelbe Männchen auf eine blaue Straße.');
      return;
    }
    const position = sv.getPosition();
    if (!position) return;

    const loc = { lat: position.lat(), lng: position.lng() };
    setConfirmOverwrite(null);

    // Optimistisches UI-Update
    setRoomState(prev => {
      if (!prev) return prev;
      const myU = Object.values(prev.users).find(u => u.name === username);
      if (!myU) return prev;
      const updated = JSON.parse(JSON.stringify(prev)) as RoomState;
      updated.users[myU.id].foundTerms[term] = loc;
      return updated;
    });

    await fetch('/api/room/mark', {
      method: 'POST',
      body: JSON.stringify({ roomCode, term, location: loc })
    });
  };

  const unmarkTerm = async (term: string) => {
    await fetch('/api/room/unmark', {
      method: 'POST',
      body: JSON.stringify({ roomCode, term })
    });
  };

  const jumpToTerm = (term: string) => {
    if (!myUser?.foundTerms[term] || !mapInstance.current) return;
    const loc = myUser.foundTerms[term];
    const sv = mapInstance.current.getStreetView();
    sv.setPosition({ lat: loc.lat, lng: loc.lng });
    sv.setVisible(true);
  };

  const startRoom = async () => {
    await fetch('/api/room/start', {
      method: 'POST',
      body: JSON.stringify({ roomCode })
    });
  };

  const finishRoom = async () => {
    await fetch('/api/room/finish', {
      method: 'POST',
      body: JSON.stringify({ roomCode })
    });
  };

  const voteResult = async (approve: boolean) => {
    await fetch('/api/room/results/vote', {
      method: 'POST',
      body: JSON.stringify({ roomCode, approve })
    });
  };

  const [resultsLocationInfo, setResultsLocationInfo] = useState<{ country: string, city: string } | null>(null);

  // Effect: Results-Map initialisieren und StreetView steuern
  const currentResultPlayerID = roomState?.resultsPlayerOrder?.[roomState?.resultsPlayerIdx ?? 0] || '';
  const currentResultTerm = roomState?.terms?.[roomState?.resultsTermIdx ?? 0] || '';
  const currentResultPlayer = roomState?.users?.[currentResultPlayerID];
  const currentResultFound = currentResultPlayer?.foundTerms?.[currentResultTerm];

  useEffect(() => {
    if (!roomState?.finished || roomState.resultsTermIdx >= roomState.terms.length) return;

    if (resultsMapRef.current && !resultsMapInstance.current && window.google?.maps) {
      resultsMapInstance.current = new window.google.maps.Map(resultsMapRef.current, {
        center: { lat: 0, lng: 0 },
        zoom: 14,
        streetViewControl: true,
        mapTypeControl: false,
        fullscreenControl: false,
        backgroundColor: '#0f141b',
        styles: DARK_MAP_STYLE,
      });
    }

    if (currentResultFound && resultsMapInstance.current) {
      const sv = resultsMapInstance.current.getStreetView();
      sv.setPosition({ lat: currentResultFound.lat, lng: currentResultFound.lng });
      sv.setVisible(true);
    }

    if (currentResultFound && window.google?.maps) {
      setResultsLocationInfo(null);
      new window.google.maps.Geocoder().geocode({ location: { lat: currentResultFound.lat, lng: currentResultFound.lng } }, (results: any, status: any) => {
        if (status === 'OK' && results?.[0]) {
          const components = results[0].address_components;
          const city = components?.find((c: any) => c.types.includes('locality'))?.long_name || '';
          const country = components?.find((c: any) => c.types.includes('country'))?.long_name || '';
          setResultsLocationInfo({ city, country });
        }
      });
    } else {
      setResultsLocationInfo(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [roomState?.resultsTermIdx, roomState?.resultsPlayerIdx, roomState?.finished]);

  // Cleanup results map when leaving results
  useEffect(() => {
    if (!roomState?.finished) {
      resultsMapInstance.current = null;
    }
  }, [roomState?.finished]);

  const copyInviteLink = async () => {
    const url = `${window.location.origin}/${roomCode}`;
    try {
      await navigator.clipboard.writeText(url);
      toast('success', 'Einladungslink kopiert.');
    } catch {
      toast('info', url);
    }
  };

  const leaveToLobby = () => {
    window.history.pushState({}, '', '/');
    setView('lobby');
    setTermsOpen(false);
    setPlayersOpen(false);
  };

  // ==========================================
  // RENDERER
  // ==========================================

  if (view === 'loading') {
    return (
      <div className="scene grain min-h-dvh flex flex-col items-center justify-center gap-4">
        <span className="w-3 h-3 rounded-full bg-accent pulse-ring" />
        <p className="font-mono text-sm text-muted tracking-wide">GeoBingo wird geladen…</p>
      </div>
    );
  }

  if (view === 'login') {
    return (
      <main className="scene grain min-h-dvh flex items-center justify-center p-5">
        <form onSubmit={handleLogin} className="panel animate-fade-up w-full max-w-md rounded-3xl p-8 sm:p-10 flex flex-col gap-6">
          <div className="flex flex-col items-center text-center gap-4">
            <BrandMark />
            <h1 className="font-display text-5xl sm:text-6xl leading-none tracking-tight">
              Geo<span className="italic text-accent">Bingo</span>
            </h1>
            <p className="text-muted text-sm max-w-xs">
              Finde Begriffe irgendwo auf Google Street View — gegen deine Freunde, in Echtzeit.
            </p>
          </div>
          <div className="flex flex-col gap-2">
            <label htmlFor="name" className="text-[0.7rem] font-medium text-faint uppercase tracking-[0.15em]">Spielername</label>
            <input
              id="name" autoFocus required value={username} maxLength={30}
              onChange={e => setUsername(e.target.value)}
              className="field" placeholder="z. B. Bartis"
            />
          </div>
          <button type="submit" className="btn btn-primary w-full">
            Loslegen <ArrowIcon className="w-4 h-4" />
          </button>
        </form>
      </main>
    );
  }

  if (view === 'lobby') {
    const seenLower = new Set<string>();
    const dupeLower = new Set<string>();
    for (const t of parsedTerms) {
      const l = t.toLowerCase();
      if (seenLower.has(l)) dupeLower.add(l);
      seenLower.add(l);
    }
    const overLimit = parsedTerms.length > 20 || termsInput.length > 500;

    return (
      <main className="scene grain min-h-dvh flex flex-col items-center justify-center p-5 gap-10">
        <header className="text-center animate-fade-up">
          <p className="text-faint text-xs uppercase tracking-[0.25em] mb-2">Willkommen zurück</p>
          <h1 className="font-display text-4xl sm:text-5xl tracking-tight">
            Hallo, <span className="italic text-accent">{username}</span>
          </h1>
        </header>

        <div className="grid w-full max-w-3xl gap-5 sm:grid-cols-2">
          {/* Raum erstellen */}
          <form onSubmit={createRoom} className="panel rounded-2xl p-6 flex flex-col gap-4 animate-fade-up" style={{ animationDelay: '.06s' }}>
            <div className="flex items-center gap-3">
              <span className="grid place-items-center w-9 h-9 rounded-xl bg-accent-deep text-accent border border-line"><FlagIcon className="w-4.5 h-4.5" /></span>
              <h2 className="text-lg font-semibold tracking-tight">Neuen Raum erstellen</h2>
            </div>
            <div className="flex flex-col gap-2">
              <label htmlFor="terms" className="text-[0.7rem] font-medium text-faint uppercase tracking-[0.15em]">Begriffe (mit Komma getrennt)</label>
              <textarea
                id="terms" name="terms" required value={termsInput}
                onChange={e => setTermsInput(e.target.value)}
                className="field h-24 resize-none"
                placeholder="Hund, Graffiti, Rote Tür, Brunnen …"
              />
            </div>

            {parsedTerms.length > 0 && (
              <div className="flex flex-wrap gap-1.5">
                {parsedTerms.slice(0, 24).map((t, i) => (
                  <span key={`${t}-${i}`} className={`chip ${dupeLower.has(t.toLowerCase()) ? 'chip-dupe' : ''}`}>{t}</span>
                ))}
              </div>
            )}

            <div className="flex items-center justify-between text-xs">
              <span className={overLimit ? 'text-danger' : 'text-faint'}>
                {parsedTerms.length}/20 Begriffe · {termsInput.length}/500 Zeichen
              </span>
            </div>
            {termsError && <p className="text-xs text-danger -mt-1">{termsError}</p>}

            <div className="flex flex-col gap-2">
              <label htmlFor="timeLimit" className="text-[0.7rem] font-medium text-faint uppercase tracking-[0.15em]">Zeitlimit</label>
              <div className="relative">
                <input id="timeLimit" name="timeLimit" type="number" min={0} defaultValue={30} className="field pr-12" />
                <span className="absolute right-3 top-1/2 -translate-y-1/2 text-xs text-faint font-mono">Min</span>
              </div>
            </div>

            <button type="submit" disabled={!!termsError} className="btn btn-primary w-full mt-1">
              Raum erstellen <ArrowIcon className="w-4 h-4" />
            </button>
          </form>

          {/* Raum beitreten */}
          <form
            onSubmit={(e) => { e.preventDefault(); joinRoom(joinCode); }}
            className="panel rounded-2xl p-6 flex flex-col gap-4 animate-fade-up self-start" style={{ animationDelay: '.12s' }}
          >
            <div className="flex items-center gap-3">
              <span className="grid place-items-center w-9 h-9 rounded-xl bg-elevated text-muted border border-line"><ArrowIcon className="w-4.5 h-4.5" /></span>
              <h2 className="text-lg font-semibold tracking-tight">Raum beitreten</h2>
            </div>
            <p className="text-sm text-muted">Hast du einen Code von einem Freund bekommen? Gib ihn hier ein.</p>
            <div className="flex flex-col gap-2">
              <label htmlFor="joinCode" className="text-[0.7rem] font-medium text-faint uppercase tracking-[0.15em]">Raum-Code</label>
              <input
                id="joinCode" value={joinCode}
                onChange={e => setJoinCode(e.target.value.toUpperCase())}
                className="field font-mono tracking-[0.2em] text-lg uppercase"
                placeholder="A1B2C3"
              />
            </div>
            <button type="submit" className="btn btn-ghost w-full mt-1">Beitreten</button>
          </form>
        </div>
      </main>
    );
  }

  if (!roomState) {
    return (
      <div className="scene grain min-h-dvh flex flex-col items-center justify-center gap-4">
        <span className="w-3 h-3 rounded-full bg-accent pulse-ring" />
        <p className="font-mono text-sm text-muted">Betrete Raum…</p>
      </div>
    );
  }

  const myUser = Object.values(roomState.users).find(u => u.name === username);
  const myFoundTerms = myUser ? Object.keys(myUser.foundTerms) : [];
  const uniqueTerms = [...new Set(roomState.terms)];
  const allFound = myUser ? uniqueTerms.every(t => myFoundTerms.includes(t)) : false;
  const allPlayers = Object.values(roomState.users).sort((a, b) => Object.keys(b.foundTerms).length - Object.keys(a.foundTerms).length);

  // ==========================================
  // AUSWERTUNG
  // ==========================================
  if (roomState.finished) {
    const isLastTerm = roomState.resultsTermIdx >= roomState.terms.length;
    const isMyTurn = myUser?.id === currentResultPlayerID;
    const iHaveVoted = myUser ? (roomState.resultsVotes && myUser.id in roomState.resultsVotes) : false;
    const voteCount = roomState.resultsVotes ? Object.keys(roomState.resultsVotes).length : 0;
    const eligibleVoters = allPlayers.length - 1;
    const playerIdx = roomState.resultsPlayerOrder?.indexOf(currentResultPlayerID) ?? 0;

    // Rangliste nach dem letzten Begriff
    if (isLastTerm) {
      const approvedCounts = allPlayers.map(user => {
        let count = 0;
        for (const term of roomState.terms) {
          if (roomState.resultsApproved?.[term]?.[user.id]) count++;
        }
        return { ...user, approvedCount: count };
      }).sort((a, b) => b.approvedCount - a.approvedCount);

      const medal = ['text-accent', 'text-muted', 'text-warn'];

      return (
        <main className="scene grain min-h-dvh flex flex-col items-center justify-center p-6 gap-7">
          <div className="flex flex-col items-center gap-3 animate-fade-up">
            <span className="grid place-items-center w-14 h-14 rounded-2xl bg-accent-deep border border-line text-accent"><TrophyIcon className="w-7 h-7" /></span>
            <h1 className="font-display text-5xl tracking-tight">Ergebnis</h1>
            <p className="text-muted text-sm"><span className="text-fg font-medium">{roomState.finishedBy}</span> hat die Runde beendet</p>
          </div>

          <div className="w-full max-w-md flex flex-col gap-2.5">
            {approvedCounts.map((user, i) => (
              <div
                key={user.id}
                className={`panel rounded-xl px-4 py-3.5 flex items-center justify-between animate-fade-up ${i === 0 ? 'ring-1 ring-accent/40' : ''}`}
                style={{ animationDelay: `${0.05 * i}s` }}
              >
                <div className="flex items-center gap-3 min-w-0">
                  <span className={`font-mono text-lg w-7 text-center ${medal[i] ?? 'text-faint'}`}>{i + 1}</span>
                  <span className="font-semibold truncate">{user.name}</span>
                </div>
                <span className="shrink-0 text-xs font-mono px-2.5 py-1 rounded-full bg-accent-deep text-accent border border-line">
                  {user.approvedCount}/{roomState.terms.length}
                </span>
              </div>
            ))}
          </div>

          <button onClick={leaveToLobby} className="btn btn-primary mt-2 px-8">Zurück zur Lobby</button>
        </main>
      );
    }

    // Auswertung: Voting-View
    return (
      <div className="h-dvh w-screen bg-ink flex flex-col overflow-hidden">
        {/* Header */}
        <div className="panel border-0 border-b rounded-none px-4 sm:px-6 py-3.5 flex items-center justify-between gap-4 z-20">
          <div className="min-w-0">
            <span className="text-[0.7rem] uppercase tracking-[0.15em] text-faint">Begriff {roomState.resultsTermIdx + 1}/{roomState.terms.length}</span>
            <h2 className="text-lg font-semibold text-accent truncate">{currentResultTerm}</h2>
          </div>
          <div className="text-center hidden sm:block min-w-0">
            {resultsLocationInfo && (
              <>
                <span className="text-[0.7rem] uppercase tracking-[0.15em] text-faint">Ort</span>
                <p className="text-sm text-muted truncate">{[resultsLocationInfo.city, resultsLocationInfo.country].filter(Boolean).join(', ') || '—'}</p>
              </>
            )}
          </div>
          <div className="text-right min-w-0 flex items-center gap-4">
            <div>
              <span className="text-[0.7rem] uppercase tracking-[0.15em] text-faint">Spieler {playerIdx + 1}/{roomState.resultsPlayerOrder?.length || 0}</span>
              <p className="font-semibold truncate">{currentResultPlayer?.name || '…'}</p>
            </div>
            {!roomState.resultsCountdown && roomState.evalTimeLeft != null && (
              <span className={`font-mono text-lg tabular-nums ${roomState.evalTimeLeft <= 15 ? 'text-danger' : 'text-muted'}`}>
                {Math.floor(roomState.evalTimeLeft / 60)}:{String(roomState.evalTimeLeft % 60).padStart(2, '0')}
              </span>
            )}
          </div>
        </div>

        {/* StreetView */}
        <div className="flex-1 relative">
          {!currentResultFound ? (
            <div className="absolute inset-0 bg-surface-2 flex items-center justify-center">
              <div className="text-center">
                <div className="text-6xl mb-3">🤷</div>
                <p className="text-lg text-muted"><span className="text-fg">{currentResultPlayer?.name || 'Spieler'}</span> hat nichts gefunden</p>
              </div>
            </div>
          ) : (
            <div ref={resultsMapRef} className="absolute inset-0 bg-surface-2" />
          )}
        </div>

        {/* Footer: Vote */}
        <div className="panel border-0 border-t rounded-none px-6 py-4 flex items-center justify-center gap-4 z-20">
          {roomState.resultsCountdown ? (
            <div className="text-accent font-medium pulse-ring rounded-full px-1">Weiter in 5 Sekunden …</div>
          ) : isMyTurn ? (
            <div className="text-faint text-sm">Du kannst nicht für dich selbst voten</div>
          ) : iHaveVoted ? (
            <div className="text-faint text-sm">Warte auf andere … ({voteCount}/{eligibleVoters})</div>
          ) : (
            <>
              <button onClick={() => voteResult(true)} className="btn btn-primary px-7"><CheckIcon className="w-4 h-4" /> Zählt</button>
              <button onClick={() => voteResult(false)} className="btn btn-danger px-7"><CloseIcon className="w-4 h-4" /> Zählt nicht</button>
              <span className="text-xs text-faint ml-1 font-mono">{voteCount}/{eligibleVoters}</span>
            </>
          )}
        </div>
      </div>
    );
  }

  // ==========================================
  // SPIEL-ANSICHT (reusable bodies)
  // ==========================================
  const termsBody = (
    <div className="flex flex-col gap-2">
      {roomState.terms.map((term, idx) => {
        const isFound = myFoundTerms.includes(term);
        return (
          <div
            key={`${term}-${idx}`}
            className={`rounded-xl text-sm transition-colors border ${isFound ? 'bg-accent-deep/60 border-accent/25' : 'bg-surface-2 border-line'}`}
          >
            <div className="flex items-center justify-between gap-2 p-2.5">
              <span className={`flex-1 min-w-0 truncate ${isFound ? 'text-accent line-through' : 'text-fg'}`}>{term}</span>
              {roomState.started && !roomState.finished && (
                <div className="flex gap-1 shrink-0">
                  {isFound ? (
                    <>
                      <button onClick={() => jumpToTerm(term)} aria-label={`Zu „${term}" springen`} title="Hinspringen"
                        className="grid place-items-center w-7 h-7 rounded-lg bg-elevated text-muted hover:text-fg transition-colors"><PinIcon className="w-3.5 h-3.5" /></button>
                      {confirmOverwrite === term ? (
                        <button onClick={() => markTermDirect(term)} title="Überschreiben bestätigen"
                          className="px-2 h-7 rounded-lg bg-warn/90 text-[#1a1206] text-xs font-semibold">Sicher?</button>
                      ) : (
                        <button onClick={() => setConfirmOverwrite(term)} aria-label={`„${term}" überschreiben`} title="Überschreiben"
                          className="grid place-items-center w-7 h-7 rounded-lg bg-elevated text-warn hover:brightness-110 transition"><EditIcon className="w-3.5 h-3.5" /></button>
                      )}
                      <button onClick={() => unmarkTerm(term)} aria-label={`„${term}" entfernen`} title="Entfernen"
                        className="grid place-items-center w-7 h-7 rounded-lg bg-elevated text-danger hover:brightness-110 transition"><TrashIcon className="w-3.5 h-3.5" /></button>
                    </>
                  ) : (
                    <button onClick={() => markTermDirect(term)} aria-label={`„${term}" hier setzen`} title="Hier setzen"
                      className="grid place-items-center w-7 h-7 rounded-lg bg-accent text-[#04160f] hover:brightness-110 transition"><PinIcon className="w-3.5 h-3.5" /></button>
                  )}
                </div>
              )}
            </div>
          </div>
        );
      })}
      {allFound && roomState.started && !roomState.finished && (
        <button onClick={finishRoom} className="btn btn-primary w-full mt-1.5"><FlagIcon className="w-4 h-4" /> Fertig!</button>
      )}
    </div>
  );

  const playersBody = (
    <div className="flex flex-col gap-2">
      {allPlayers.map(user => {
        const isMe = user.id === myUser?.id;
        return (
          <div key={user.id} className={`flex items-center justify-between gap-2 px-3 py-2.5 rounded-xl border ${isMe ? 'bg-accent-deep/40 border-accent/25' : 'bg-surface-2 border-line'}`}>
            <span className="font-medium truncate flex items-center gap-2">
              {user.name}
              {user.id === roomState.creatorId && <span className="text-[0.6rem] uppercase tracking-wider text-faint border border-line rounded px-1 py-0.5">Host</span>}
            </span>
            <span className="shrink-0 text-xs font-mono px-2 py-0.5 rounded-full bg-elevated text-accent border border-line">
              {Object.keys(user.foundTerms).length}
            </span>
          </div>
        );
      })}
    </div>
  );

  return (
    <div className="h-dvh w-screen overflow-hidden bg-ink flex">
      {/* Desktop: Begriffe-Sidebar */}
      <aside className="hidden lg:flex w-72 shrink-0 flex-col border-r border-line bg-surface/50">
        <div className="p-4 border-b border-line">
          <div className="flex items-center justify-between mb-1">
            <h2 className="font-semibold text-accent">Begriffe</h2>
            <button onClick={leaveToLobby} className="text-xs text-faint hover:text-fg transition-colors">Lobby verlassen</button>
          </div>
          <p className="text-xs text-faint font-mono">{myFoundTerms.length}/{uniqueTerms.length} gefunden</p>
        </div>
        <div className="flex-1 overflow-y-auto p-3">{termsBody}</div>
      </aside>

      {/* Karten-Bereich */}
      <main className="relative flex-1 min-w-0">
        <div ref={mapRef} className="absolute inset-0 bg-surface-2" />

        {/* Suchleiste */}
        <div className="absolute top-3 lg:top-4 left-1/2 -translate-x-1/2 z-10 w-[min(90vw,24rem)] px-2 lg:px-0">
          <div className="relative">
            <SearchIcon className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-faint pointer-events-none" />
            <label htmlFor="placeSearch" className="sr-only">Ort suchen</label>
            <input
              id="placeSearch" type="text" placeholder="Ort suchen (Enter)…"
              className="field pl-9 backdrop-blur-md bg-surface/80"
              onKeyDown={(e) => {
                if (e.key === 'Enter' && window.google) {
                  const geocoder = new window.google.maps.Geocoder();
                  geocoder.geocode({ address: e.currentTarget.value }, (results: any, status: any) => {
                    if (status === 'OK' && results && results[0]) {
                      mapInstance.current?.setCenter(results[0].geometry.location);
                      mapInstance.current?.setZoom(14);
                    } else toast('warn', 'Ort nicht gefunden.');
                  });
                }
              }}
            />
          </div>
        </div>

        {/* Mobile: Top-Bar */}
        <div className="lg:hidden absolute top-0 inset-x-0 z-20 flex items-center gap-2 p-2.5 pt-3 bg-linear-to-b from-ink/95 via-ink/60 to-transparent">
          <button onClick={leaveToLobby} aria-label="Lobby verlassen" className="grid place-items-center w-9 h-9 rounded-xl panel text-muted"><CloseIcon className="w-4 h-4" /></button>
          <div className="panel rounded-xl px-3 h-9 flex items-center gap-2">
            <span className="font-mono text-accent text-sm">{roomState.code}</span>
            <span className="text-faint text-xs font-mono">{myFoundTerms.length}/{uniqueTerms.length}</span>
          </div>
          {timeRemaining !== null && (
            <div className={`panel rounded-xl px-3 h-9 flex items-center font-mono text-sm tabular-nums ${timeRemaining <= 60 ? 'text-danger' : 'text-accent'}`}>
              {Math.floor(timeRemaining / 60)}:{String(timeRemaining % 60).padStart(2, '0')}
            </div>
          )}
          <div className="flex-1" />
          <button onClick={copyInviteLink} aria-label="Einladungslink kopieren" className="grid place-items-center w-9 h-9 rounded-xl panel text-muted"><LinkIcon className="w-4 h-4" /></button>
          <button onClick={() => setPlayersOpen(true)} className="panel rounded-xl px-3 h-9 flex items-center gap-1.5 text-sm text-muted">
            <UsersIcon className="w-4 h-4" /> {allPlayers.length}
          </button>
        </div>

        {/* Mobile: Begriffe Bottom-Sheet */}
        <div className="lg:hidden absolute bottom-0 inset-x-0 z-30">
          <div className="panel rounded-t-2xl border-b-0">
            <button onClick={() => setTermsOpen(o => !o)} aria-expanded={termsOpen}
              className="w-full flex items-center justify-between px-4 py-3">
              <span className="font-semibold text-accent flex items-center gap-2">
                Begriffe <span className="text-faint font-mono text-xs">{myFoundTerms.length}/{uniqueTerms.length}</span>
              </span>
              <ChevronIcon className={`w-4 h-4 text-muted transition-transform ${termsOpen ? 'rotate-180' : ''}`} />
            </button>
            {termsOpen && <div className="max-h-[44vh] overflow-y-auto px-3 pb-4 animate-sheet-up">{termsBody}</div>}
          </div>
        </div>

        {/* Mobile: Spieler-Drawer */}
        {playersOpen && (
          <div className="lg:hidden fixed inset-0 z-40">
            <button aria-label="Schließen" onClick={() => setPlayersOpen(false)} className="absolute inset-0 bg-black/60" />
            <div className="absolute right-0 top-0 bottom-0 w-72 max-w-[85vw] panel rounded-l-2xl border-r-0 p-4 flex flex-col animate-drawer-in">
              <div className="flex items-center justify-between mb-1">
                <h2 className="font-semibold">Raum {roomState.code}</h2>
                <button onClick={() => setPlayersOpen(false)} aria-label="Schließen" className="grid place-items-center w-8 h-8 rounded-lg bg-elevated text-muted"><CloseIcon className="w-4 h-4" /></button>
              </div>
              <button onClick={copyInviteLink} className="btn btn-ghost w-full text-sm my-3"><LinkIcon className="w-4 h-4" /> Link kopieren</button>
              <div className="flex-1 overflow-y-auto">{playersBody}</div>
            </div>
          </div>
        )}

        {/* Warte-Overlay */}
        {!roomState.started && (
          <div className="absolute inset-0 z-30 bg-ink/80 backdrop-blur-sm flex items-center justify-center p-5">
            <div className="panel rounded-2xl p-7 text-center max-w-sm w-full animate-pop-in">
              <span className="inline-grid place-items-center w-12 h-12 rounded-2xl bg-accent-deep border border-line text-accent mb-4"><UsersIcon className="w-6 h-6" /></span>
              <h2 className="text-xl font-semibold mb-1">Warte auf Start</h2>
              <p className="text-faint text-sm mb-4 font-mono">{Object.keys(roomState.users).length} Spieler im Raum</p>
              <div className="flex flex-wrap justify-center gap-1.5 mb-5">
                {Object.values(roomState.users).map(u => (
                  <span key={u.id} className="chip">{u.name}</span>
                ))}
              </div>
              <button onClick={copyInviteLink} className="btn btn-ghost w-full text-sm mb-3"><LinkIcon className="w-4 h-4" /> Einladungslink kopieren</button>
              {myUser?.id === roomState.creatorId ? (
                <button onClick={startRoom} className="btn btn-primary w-full">Spiel starten <ArrowIcon className="w-4 h-4" /></button>
              ) : (
                <p className="text-faint text-sm">Der Ersteller startet das Spiel …</p>
              )}
            </div>
          </div>
        )}
      </main>

      {/* Desktop: Raum/Spieler-Sidebar */}
      <aside className="hidden lg:flex w-72 shrink-0 flex-col border-l border-line bg-surface/50">
        <div className="p-4 border-b border-line">
          <div className="flex items-center justify-between mb-2">
            <h2 className="font-semibold">Raum <span className="font-mono text-accent">{roomState.code}</span></h2>
            {timeRemaining !== null && (
              <span className={`font-mono text-sm tabular-nums ${timeRemaining <= 60 ? 'text-danger' : 'text-accent'}`}>
                {Math.floor(timeRemaining / 60)}:{String(timeRemaining % 60).padStart(2, '0')}
              </span>
            )}
          </div>
          <button onClick={copyInviteLink} className="btn btn-ghost w-full text-sm py-2"><LinkIcon className="w-4 h-4" /> Link kopieren</button>
        </div>
        <div className="px-4 pt-3 pb-1 text-xs text-faint uppercase tracking-[0.15em]">Spieler · {allPlayers.length}</div>
        <div className="flex-1 overflow-y-auto p-3 pt-2">{playersBody}</div>
      </aside>
    </div>
  );
}
