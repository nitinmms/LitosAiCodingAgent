# Google OAuth verification for the Gmail connector

What to enter in the Google Cloud console to get the Litos Gmail client verified, so any Google
account (not only listed test users) can connect Gmail to a Buzzy in Litos.GUI. The client is the
"Desktop app" client in `src/Litos.Mail/BuiltInGmailClient.cs`. Its secret is never committed;
release builds read it from the repository secret `LITOS_GMAIL_CLIENT_SECRET`.

Submit only the two scopes the released app uses today. `gmail.modify` (organise and send) needs a
separate review once those features exist, because Google wants each scope shown in use.

## 1. Before you submit

- [ ] `litosai.dev` is verified in [Google Search Console](https://search.google.com/search-console)
      by the Google account that owns the Cloud project.
- [ ] These are live and load in a normal browser:
  - `https://litosai.dev/`, which describes Litos and has the Buzzies and Gmail section
  - `https://litosai.dev/privacy/`, which has the "Gmail and Google user data" and "Limited Use" sections
  - `https://litosai.dev/terms/`
- [ ] `support@litosai.dev` receives mail (Hostinger mailbox or forwarder).

## 2. Google Auth Platform → Branding

| Field | Value |
|---|---|
| App name | Litos |
| User support email | support@litosai.dev |
| App logo | `assets/icon.png` from the website (120×120 px, square). Adding a logo starts a brand review; that's expected. |
| Application home page | https://litosai.dev/ |
| Application privacy policy link | https://litosai.dev/privacy/ |
| Application terms of service link | https://litosai.dev/terms/ |
| Authorized domains | litosai.dev |
| Developer contact information | support@litosai.dev |

## 3. Google Auth Platform → Data Access

Keep exactly these two scopes:

- `https://www.googleapis.com/auth/gmail.readonly`
- `https://www.googleapis.com/auth/gmail.compose`

### Scope justification: gmail.readonly

> Litos is an open-source desktop application. In its Litos.GUI app, users create "Buzzies":
> AI assistants that run on the user's own computer. When a user connects Gmail, a Buzzy can search
> the user's mailbox, read messages and, after the user approves each one, read an attachment, in
> order to answer the user's own requests (for example "summarise today's unread mail" or "what does
> the invoice from Priya say?") or run a routine the user scheduled. gmail.readonly is the narrowest
> scope that allows searching and reading message bodies and attachments. Litos has no feature that
> sends, deletes, archives, moves or marks mail as read. Message content is processed on the user's
> computer and sent only to the AI model provider the user configured with their own API key; it is
> never sent to servers operated by Litos, and Litos operates none for this purpose.

### Scope justification: gmail.compose

> Requested only when the user ticks "Also let Buzzies save draft replies". A Buzzy then proposes a
> reply, the user sees the full draft in an approval card and approves it, and Litos saves it as a
> draft in the user's Gmail for the user to review and send themselves. gmail.compose is the
> narrowest scope that allows creating drafts. Litos does not send email: the app's mail interface
> has no send operation, and drafts are never sent by Litos.

### How the data is used (if asked)

> Gmail data is used only to provide the user-facing features above, at the user's request. It is not
> sold, not used for advertising, not used to train or improve AI/ML models, and not read by any
> person working on Litos. It is transferred only to the AI model provider the user selected, to
> process that user's request. Sign-in tokens are stored encrypted on the user's computer (Windows
> DPAPI). Users can disconnect in the app, which deletes the token, or revoke access at
> myaccount.google.com/permissions. Our use complies with the Google API Services User Data Policy,
> including the Limited Use requirements.

## 4. Demo video (unlisted YouTube, about 3–4 minutes)

Record on Windows with the released Litos.GUI build. Keep the browser's address bar visible
during the consent step, because Google checks the `client_id` in the URL against the project.

1. **Litos and the website (15 s).** Show `https://litosai.dev/`, scroll to the Buzzies section,
   open the privacy policy's Gmail section.
2. **Connect (45 s).** Open Litos.GUI → **Gmail** in the sidebar. Tick **Also let Buzzies save
   draft replies**. Press **Connect Gmail…**. In the browser, show the URL including
   `client_id=…apps.googleusercontent.com`, choose the account, show both permissions on the consent
   screen, tick both, Continue. Show "Litos is connected to Gmail", then the Litos window reading
   **Connected: …**.
3. **Grant a Buzzy access (20 s).** Right-click a Buzzy → **Access and model…** → tick the email
   options. Point out that each Buzzy only gets email if it is turned on here.
4. **gmail.readonly in use (60 s).** Ask the Buzzy *"Summarise my three most recent emails."* Show
   the tool calls (`mail_search`, `mail_read`) and the answer. Then ask it to read a PDF
   attachment: show the approval card, approve it, show the summary.
5. **gmail.compose in use (45 s).** Ask *"Draft a short reply to the latest email saying thanks."*
   Show the approval card with the full draft, approve, then open Gmail in the browser and show the
   draft in **Drafts**, unsent.
6. **Disconnect and revoke (20 s).** In Litos press **Disconnect**. Optionally show Litos listed at
   `myaccount.google.com/permissions` and removing it.

Narrate or caption each step. Use a test mailbox with no real personal data.

## 5. Submit

Google Auth Platform → **Audience** → **Publish app** (moves from Testing to In production), then
**Verification Center** → **Prepare for verification** → paste the justifications and the video
link → Submit. Expect emails from Google's reviewers. Because both scopes are restricted, Google
will probably also ask for a security assessment (CASA) by an authorised assessor. Reply on the
thread the review opens.

While the review runs, already-listed test users keep working. Unverified production apps are
capped at 100 users and show the "unverified app" warning.
