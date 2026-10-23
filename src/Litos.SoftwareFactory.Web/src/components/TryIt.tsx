import { useState } from 'react';
import type { RunInstructions } from '../api/types';
import type { TryItGuide } from '../domain/tryIt';
import { SafeLink } from './bits';

/**
 * What a tester who has never seen the branch needs, in the order they need it: where to get the
 * code, how to run it, what to test, and how to answer. The work is not merged, so it is tested
 * from the pushed branch on the tester's own machine, never from the factory's working copy.
 */
export function TryItYourself({
  guide,
  branch,
  pullRequest,
  howToRun,
  tests,
  answer,
}: {
  guide: TryItGuide;
  branch: string;
  /** The pull request link, as the card already labels it; null when there is none. */
  pullRequest: React.ReactNode;
  howToRun: RunInstructions | null | undefined;
  /** The manual test checklist; null when the agent named none. */
  tests: React.ReactNode;
  /** Accept and Request changes; null once the handoff is no longer waiting. */
  answer: React.ReactNode;
}) {
  const [fresh, setFresh] = useState(true);

  return (
    <section className="tryit" aria-label="Try it yourself">
      <div className="stack-tight">
        <h3>Try it yourself</h3>
        <p className="muted">This change is not merged. It is on its own branch on GitHub, so you test it on your own machine.</p>
      </div>

      <TryItStep n={1} title="Get the code">
        <dl className="kv">
          <dt>Repository</dt>
          <dd>
            <SafeLink href={guide.repositoryUrl}>{guide.repository}</SafeLink>
          </dd>
          <dt>Branch</dt>
          <dd className="mono">{branch}</dd>
        </dl>
        <div className="btn-row">
          <SafeLink href={guide.branchUrl}>View branch on GitHub</SafeLink>
          {guide.compareUrl ? <SafeLink href={guide.compareUrl}>Compare with the base branch</SafeLink> : null}
          {pullRequest}
        </div>
        <div className="tabs" role="tablist" aria-label="How to get the branch">
          <button role="tab" type="button" aria-selected={fresh} onClick={() => setFresh(true)}>
            I don't have the repository
          </button>
          <button role="tab" type="button" aria-selected={!fresh} onClick={() => setFresh(false)}>
            I already have a clone
          </button>
        </div>
        <div role="tabpanel" className="stack-tight">
          <p className="small muted">
            {fresh
              ? 'Run these in the folder where you keep your projects. They make a new folder just for this task.'
              : 'Run these inside your clone. Commit or stash anything you are working on first.'}
          </p>
          {(fresh ? guide.freshCommands : guide.existingCloneCommands).map((command) => (
            <CommandLine key={command} command={command} />
          ))}
        </div>
        <p className="small muted">You need git, and read access to the repository on GitHub.</p>
      </TryItStep>

      <TryItStep
        n={2}
        title="Run it"
        tag={howToRun?.steps.length ? <span className="tag">Suggested by the agent · not run by the factory</span> : null}
      >
        <RunIt howToRun={howToRun} readmeUrl={guide.readmeUrl} />
      </TryItStep>

      <TryItStep n={3} title="Test it">
        {tests ?? <p className="small muted">The agent named no manual tests. Try the change the request describes.</p>}
      </TryItStep>

      {answer ? (
        <TryItStep n={4} title="Tell the factory">
          {answer}
          <p className="small muted">Accepting does not merge the branch. That is still done on GitHub.</p>
        </TryItStep>
      ) : null}
    </section>
  );
}

function TryItStep({ n, title, tag, children }: { n: number; title: string; tag?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="tryit-step">
      <span className="tryit-num" aria-hidden="true">
        {n}
      </span>
      <div className="stack">
        <div className="section-head">
          <h4>{title}</h4>
          {tag}
        </div>
        {children}
      </div>
    </div>
  );
}

function RunIt({ howToRun, readmeUrl }: { howToRun: RunInstructions | null | undefined; readmeUrl: string }) {
  const prerequisites = howToRun?.prerequisites ?? [];
  const steps = howToRun?.steps ?? [];

  return (
    <>
      {prerequisites.length ? (
        <p className="small">
          <span className="muted">Needs:</span> {prerequisites.map((p) => p.replace(/\.$/, '')).join('; ')}.
        </p>
      ) : null}
      {steps.length ? (
        <ol className="tryit-run">
          {steps.map((step, i) => (
            <li key={i}>
              {step.text ? <span>{step.text}</span> : null}
              {step.command ? <CommandLine command={step.command} /> : null}
            </li>
          ))}
        </ol>
      ) : (
        <div className="tryit-none">
          <p>The agent didn't say how to run this project.</p>
          <p className="small muted">
            The project's README on this branch usually says how. If it doesn't, ask with Request changes, for example: "Tell
            me how to run this."
          </p>
          <SafeLink href={readmeUrl}>Open the README on this branch</SafeLink>
        </div>
      )}
      {howToRun?.setupChanges ? (
        <p className="tryit-setup small">
          <b>Changed by this task:</b> {howToRun.setupChanges}
        </p>
      ) : null}
    </>
  );
}

/** One command to type, with a button that copies it. */
function CommandLine({ command }: { command: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // No clipboard (an insecure origin, say): the command is still there to select by hand.
    }
  };

  return (
    <div className="cmd">
      <code>{command}</code>
      <button className="btn small" type="button" onClick={copy} aria-label={`Copy: ${command}`}>
        {copied ? 'Copied' : 'Copy'}
      </button>
    </div>
  );
}
