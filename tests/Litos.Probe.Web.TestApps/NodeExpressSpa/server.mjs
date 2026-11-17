// Express serving the built SPA from dist/ and a small notes API over a SQLite file.
import path from "node:path";
import express from "express";
import { DatabaseSync } from "node:sqlite";

const port = Number(process.env.PORT ?? 3000);
const db = new DatabaseSync(process.env.NOTES_DB ?? "data/notes.db");
const app = express();
app.use(express.json());

app.get("/api/notes", (_req, res) => res.json(db.prepare("SELECT id, text FROM notes ORDER BY id").all()));
app.post("/api/notes", (req, res) => {
  const text = String(req.body?.text ?? "").trim();
  if (!text) return res.status(400).json({ error: "text is required" });
  const { lastInsertRowid } = db.prepare("INSERT INTO notes (text) VALUES (?)").run(text);
  res.status(201).json({ id: Number(lastInsertRowid), text });
});

// Test-only endpoints, registered only under NODE_ENV=test (Gate step 7 checks they are gone in Production).
if (process.env.NODE_ENV === "test") {
  let now = null;
  app.post("/__test/clock", (req, res) => { now = req.body?.now ?? null; res.json({ now }); });
  app.get("/api/now", (_req, res) => res.json({ now: now ?? new Date().toISOString() }));
}

const dist = path.resolve("dist");
app.use(express.static(dist));
// The SPA's own routes: any other GET that isn't the API gets index.html.
app.get(/^\/(?!api\/|__test\/).*/, (_req, res) => res.sendFile(path.join(dist, "index.html")));

app.listen(port, "127.0.0.1", () => console.log(`notes app on http://localhost:${port}`));
