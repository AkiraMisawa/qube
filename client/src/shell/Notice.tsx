import { useEffect, useState } from 'react';
import { Notification } from '@mantine/core';
import { IconCheck, IconX } from '@tabler/icons-react';
import { DONE_NOTICE_MS, type Notice } from '../platform/notices';

/**
 * The notice in view (ADR-0006's note 2 of 2026-11-11; docs/design-guidelines.md §6), drawn over the top of the main
 * area in a strip of no height, so the page under it does not move. A done one leaves by itself unless the pointer or
 * the keyboard is on it; a refused one stays until it is closed or another replaces it.
 */
export function NoticeStrip({ notice, onClose }: { readonly notice: Notice | undefined; readonly onClose: () => void }) {
  const [held, setHeld] = useState(false);
  useEffect(() => {
    if (notice === undefined || notice.kind !== 'done' || held) return undefined;
    const timer = setTimeout(onClose, DONE_NOTICE_MS);
    return () => clearTimeout(timer);
  }, [notice, held, onClose]);
  // The pointer and the keyboard leave with the notice they were on.
  useEffect(() => { if (notice === undefined) setHeld(false); }, [notice]);

  if (notice === undefined) return null;
  const done = notice.kind === 'done';
  return (
    <div className="qube-notice-strip">
      <Notification className="qube-notice" data-testid="notice" data-kind={notice.kind} role={done ? 'status' : 'alert'}
        color={done ? 'teal' : 'red'} withBorder onClose={onClose} closeButtonProps={{ 'aria-label': 'Close the notice' }}
        icon={done ? <IconCheck size={16} stroke={1.75} aria-hidden /> : <IconX size={16} stroke={1.75} aria-hidden />}
        onPointerEnter={() => setHeld(true)} onPointerLeave={() => setHeld(false)} onFocus={() => setHeld(true)} onBlur={() => setHeld(false)}>
        {notice.message}
      </Notification>
    </div>
  );
}
