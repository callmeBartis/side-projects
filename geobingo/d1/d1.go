// Package d1 is a database/sql driver for Cloudflare D1, reached through the
// data-proxy Worker (POST /query for reads, POST /batch for writes).
//
// Transactions are buffered: Exec inside a Tx only queues the statement and
// Commit sends the queue as one /batch, which D1 runs atomically. Reads inside
// a Tx are therefore impossible and return an error.
package d1

import (
	"bytes"
	"context"
	"database/sql/driver"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

// Connector holds the Worker endpoint, e.g.
// https://data-proxy.example.workers.dev/v1/geobingo. Copied from
// family-friends/openair-schlanstedt/internal/d1; keep the two in sync. Use with sql.OpenDB.
type Connector struct {
	URL    string
	Token  string
	Client *http.Client
}

func NewConnector(url, token string) *Connector {
	return &Connector{
		URL:    strings.TrimRight(url, "/"),
		Token:  token,
		Client: &http.Client{Timeout: 30 * time.Second},
	}
}

func (c *Connector) Connect(context.Context) (driver.Conn, error) { return &conn{c: c}, nil }
func (c *Connector) Driver() driver.Driver                        { return drv{} }

type drv struct{}

func (drv) Open(string) (driver.Conn, error) {
	return nil, errors.New("d1: use sql.OpenDB(d1.NewConnector(...))")
}

type statement struct {
	SQL    string `json:"sql"`
	Params []any  `json:"params"`
}

type batchResult struct {
	Changes   int64 `json:"changes"`
	LastRowID int64 `json:"lastRowId"`
}

func (c *Connector) post(ctx context.Context, op string, body, out any) error {
	buf, err := json.Marshal(body)
	if err != nil {
		return fmt.Errorf("d1: encode request: %w", err)
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.URL+"/"+op, bytes.NewReader(buf))
	if err != nil {
		return fmt.Errorf("d1: build request: %w", err)
	}
	req.Header.Set("Authorization", "Bearer "+c.Token)
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.Client.Do(req)
	if err != nil {
		return fmt.Errorf("d1: %s: %w", op, err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		var e struct {
			Error string `json:"error"`
		}
		raw, _ := io.ReadAll(io.LimitReader(resp.Body, 64<<10))
		if json.Unmarshal(raw, &e) != nil || e.Error == "" {
			e.Error = strings.TrimSpace(string(raw))
		}
		return fmt.Errorf("d1: %s: HTTP %d: %s", op, resp.StatusCode, e.Error)
	}
	dec := json.NewDecoder(resp.Body)
	dec.UseNumber()
	if err := dec.Decode(out); err != nil {
		return fmt.Errorf("d1: decode %s response: %w", op, err)
	}
	return nil
}

func (c *Connector) batch(ctx context.Context, stmts []statement) ([]batchResult, error) {
	var out struct {
		Results []batchResult `json:"results"`
	}
	if err := c.post(ctx, "batch", map[string]any{"statements": stmts}, &out); err != nil {
		return nil, err
	}
	if len(out.Results) != len(stmts) {
		return nil, fmt.Errorf("d1: batch returned %d results for %d statements", len(out.Results), len(stmts))
	}
	return out.Results, nil
}

func newStatement(query string, args []driver.NamedValue) (statement, error) {
	params := make([]any, len(args))
	for i, a := range args {
		if a.Name != "" {
			return statement{}, fmt.Errorf("d1: named parameter %q not supported", a.Name)
		}
		switch v := a.Value.(type) {
		case nil, int64, float64, string:
			params[i] = v
		case int:
			params[i] = int64(v)
		case bool:
			if v {
				params[i] = int64(1)
			} else {
				params[i] = int64(0)
			}
		default:
			return statement{}, fmt.Errorf("d1: parameter %d: unsupported type %T (only int, float, string, bool, nil)", a.Ordinal, a.Value)
		}
	}
	return statement{SQL: query, Params: params}, nil
}

type conn struct {
	c  *Connector
	tx *tx
}

func (cn *conn) Prepare(query string) (driver.Stmt, error) { return &stmt{cn: cn, query: query}, nil }
func (cn *conn) Close() error                              { return nil }
func (cn *conn) Begin() (driver.Tx, error) {
	return cn.BeginTx(context.Background(), driver.TxOptions{})
}

func (cn *conn) BeginTx(context.Context, driver.TxOptions) (driver.Tx, error) {
	if cn.tx != nil {
		return nil, errors.New("d1: transaction already open")
	}
	cn.tx = &tx{cn: cn}
	return cn.tx, nil
}

func (cn *conn) Ping(ctx context.Context) error {
	_, err := cn.QueryContext(ctx, "SELECT 1", nil)
	return err
}

func (cn *conn) ExecContext(ctx context.Context, query string, args []driver.NamedValue) (driver.Result, error) {
	s, err := newStatement(query, args)
	if err != nil {
		return nil, err
	}
	if cn.tx != nil {
		cn.tx.stmts = append(cn.tx.stmts, s)
		return txResult{}, nil
	}
	res, err := cn.c.batch(ctx, []statement{s})
	if err != nil {
		return nil, err
	}
	return result(res[0]), nil
}

func (cn *conn) QueryContext(ctx context.Context, query string, args []driver.NamedValue) (driver.Rows, error) {
	if cn.tx != nil {
		return nil, errors.New("d1: reads inside transactions are not supported by d1 driver")
	}
	s, err := newStatement(query, args)
	if err != nil {
		return nil, err
	}
	var out struct {
		Columns []string `json:"columns"`
		Rows    [][]any  `json:"rows"`
	}
	if err := cn.c.post(ctx, "query", s, &out); err != nil {
		return nil, err
	}
	data := make([][]driver.Value, len(out.Rows))
	for r, row := range out.Rows {
		data[r] = make([]driver.Value, len(row))
		for i, v := range row {
			cv, err := convertValue(v)
			if err != nil {
				return nil, err
			}
			data[r][i] = cv
		}
	}
	return &rows{cols: out.Columns, data: data}, nil
}

func convertValue(v any) (driver.Value, error) {
	switch x := v.(type) {
	case nil, string, bool:
		return x, nil
	case json.Number:
		if n, err := x.Int64(); err == nil {
			return n, nil
		}
		f, err := x.Float64()
		if err != nil {
			return nil, fmt.Errorf("d1: bad number %q: %w", x, err)
		}
		return f, nil
	default:
		return nil, fmt.Errorf("d1: unsupported column value type %T", v)
	}
}

type tx struct {
	cn    *conn
	stmts []statement
}

func (t *tx) Commit() error {
	t.cn.tx = nil
	if len(t.stmts) == 0 {
		return nil
	}
	_, err := t.cn.c.batch(context.Background(), t.stmts)
	return err
}

func (t *tx) Rollback() error {
	t.cn.tx = nil
	return nil
}

type result batchResult

func (r result) LastInsertId() (int64, error) { return r.LastRowID, nil }
func (r result) RowsAffected() (int64, error) { return r.Changes, nil }

// txResult is returned for buffered Exec calls: nothing has run yet.
type txResult struct{}

var errTxResult = errors.New("d1: result unavailable inside a transaction (statement is sent on Commit)")

func (txResult) LastInsertId() (int64, error) { return 0, errTxResult }
func (txResult) RowsAffected() (int64, error) { return 0, errTxResult }

type rows struct {
	cols []string
	data [][]driver.Value
	i    int
}

func (r *rows) Columns() []string { return r.cols }
func (r *rows) Close() error      { return nil }

func (r *rows) Next(dest []driver.Value) error {
	if r.i >= len(r.data) {
		return io.EOF
	}
	copy(dest, r.data[r.i])
	r.i++
	return nil
}

type stmt struct {
	cn    *conn
	query string
}

func (s *stmt) Close() error  { return nil }
func (s *stmt) NumInput() int { return -1 }

func (s *stmt) ExecContext(ctx context.Context, args []driver.NamedValue) (driver.Result, error) {
	return s.cn.ExecContext(ctx, s.query, args)
}

func (s *stmt) QueryContext(ctx context.Context, args []driver.NamedValue) (driver.Rows, error) {
	return s.cn.QueryContext(ctx, s.query, args)
}

func (s *stmt) Exec(args []driver.Value) (driver.Result, error) {
	return s.ExecContext(context.Background(), named(args))
}

func (s *stmt) Query(args []driver.Value) (driver.Rows, error) {
	return s.QueryContext(context.Background(), named(args))
}

func named(args []driver.Value) []driver.NamedValue {
	out := make([]driver.NamedValue, len(args))
	for i, v := range args {
		out[i] = driver.NamedValue{Ordinal: i + 1, Value: v}
	}
	return out
}
