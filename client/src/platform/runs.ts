// A Run's own actions, whichever page offers them (ADR-0010 §7, docs/05): Download saves the Run as the server's zip of
// its record, its frozen inputs, its results and its lineage. The page builds nothing of it.

/** Where a Run's archive is read: a zip named after the Run. */
export const archiveUrl = (runId: string): string => `/api/runs/${encodeURIComponent(runId)}/archive`;

/** Saves a Run's archive, as the Download control's own link does, for a command that has no link to press. */
export function downloadRun(runId: string): void {
  const link = document.createElement('a');
  link.href = archiveUrl(runId);
  link.download = `${runId}.zip`;
  link.click();
}

/**
 * Puts the same content on the clipboard as HTML and as plain text, so a mail takes the table and a chat the lines.
 * Where the browser cannot hold both, it is refused rather than copied half.
 */
export async function copyRich(text: string, html: string): Promise<void> {
  if (typeof ClipboardItem === 'undefined') throw new Error('This browser cannot put HTML on the clipboard.');
  await navigator.clipboard.write([new ClipboardItem({
    'text/plain': new Blob([text], { type: 'text/plain' }),
    'text/html': new Blob([html], { type: 'text/html' }),
  })]);
}
