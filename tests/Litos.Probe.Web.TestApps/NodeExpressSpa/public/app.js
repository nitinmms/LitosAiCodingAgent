// A tiny client-side router: / lists and adds notes, /about is static. Every control has a label
// or an accessible name, so the Gate's lint (step 8) passes.
const view = document.getElementById("view");

async function notes() {
  view.innerHTML = `
    <h1>Notes</h1>
    <ul data-testid="notes"></ul>
    <form id="add">
      <label for="text">New note</label>
      <label class="detail"><span>Note detail</span><textarea id="detail" name="detail"></textarea></label>
      <input id="text" name="text" required>
      <button type="submit">Add note</button>
    </form>`;
  // A controlled textarea's value is its child text node, as React writes it, so the wrapping
  // label's textContent reads the label followed by the value (Probe.Web W3.8 finding 14).
  const detail = view.querySelector("#detail");
  detail.addEventListener("input", () => { detail.textContent = detail.value; });
  const list = view.querySelector("ul");
  const render = (items) => { list.innerHTML = items.map((n) => `<li>${n.text.replace(/</g, "&lt;")}</li>`).join(""); };
  render(await (await fetch("/api/notes")).json());
  view.querySelector("#add").addEventListener("submit", async (e) => {
    e.preventDefault();
    const input = view.querySelector("#text");
    await fetch("/api/notes", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ text: input.value }) });
    input.value = "";
    render(await (await fetch("/api/notes")).json());
  });
}

function about() {
  view.innerHTML = `<h1>About</h1><p>A test app for Litos.Probe.Web.</p>`;
}

function route() {
  (location.pathname === "/about" ? about : notes)();
}

document.addEventListener("click", (e) => {
  const link = e.target.closest("a[data-link]");
  if (!link) return;
  e.preventDefault();
  history.pushState(null, "", link.getAttribute("href"));
  route();
});
window.addEventListener("popstate", route);
route();
