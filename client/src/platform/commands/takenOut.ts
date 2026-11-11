// Where `copy` and `export` put what they take out of the workbench (ADR-0016 note 44 of 2026-11-11): the browser's
// clipboard, and a file the browser saves. A page's door is given these, so that a test can stand in for the browser.

/** Where a door puts the cells the server answered: the clipboard, which may refuse them, and a file saved under its name. */
export interface TakenOut {
  /** Puts `text` on the clipboard, or throws with why the browser did not take it. */
  copy(text: string): Promise<void>;
  /** Saves `text` as a file the browser names `fileName`. */
  save(fileName: string, text: string): void;
}

export const browserOut: TakenOut = {
  copy: async (text) => {
    // A browser gives a page the clipboard only over https or on localhost: elsewhere there is none to ask.
    if (!window.isSecureContext) throw new Error('this page is served over neither https nor localhost, and a browser gives its clipboard to no other page. export saves the same rows as a file.');
    await navigator.clipboard.writeText(text);
  },
  save: (fileName, text) => {
    // The text holds its own byte order mark, which a Blob writes as UTF-8 with the rest.
    const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.click();
    // The press read the URL's blob as it followed the link (File API, "blob URL entry"), so it is let go at once.
    URL.revokeObjectURL(url);
  },
};
