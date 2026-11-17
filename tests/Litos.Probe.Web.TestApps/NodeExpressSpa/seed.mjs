// Writes data/notes.db with known rows: the Gate's sqlite-snapshot reset keeps a copy of it and
// restores it before every start.
import fs from "node:fs";
import { DatabaseSync } from "node:sqlite";
fs.mkdirSync("data", { recursive: true });
const db = new DatabaseSync("data/notes.db");
db.exec("DROP TABLE IF EXISTS notes; CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT NOT NULL)");
const insert = db.prepare("INSERT INTO notes (text) VALUES (?)");
for (const text of ["Buy flour", "Feed the starter", "Book the oven"]) insert.run(text);
db.close();
console.log("seeded data/notes.db");
