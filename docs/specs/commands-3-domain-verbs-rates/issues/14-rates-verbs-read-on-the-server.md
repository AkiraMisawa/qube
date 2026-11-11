# 14: Rates' verbs are read on the server

**What to build:** `new <product>`, `live start`, `live stop`, `capture` and `price` say how they are typed, for
Rates' application. The client's hand-written Rates commands go. The three defects of 2026-11-10 end: a partial `set
defaults`, the raw stack trace, and no command that edits or removes a line.

**Pull request:** 3c

**Blocked by:** 13.

**Status:** done

- [x] `new irs …` previews the line it would make from the terms typed.
- [x] `live start`, `live stop` and `capture` do what they did, through the door.
- [x] No refusal shows a stack trace.

## Comments

- *As built (ADR-0016 note 36):* `rates.workspace.price`, `rates.frame.capture`, `rates.line.add` (`add line
  <product>`, which the ticket did not name and the page's two buttons needed), `rates.live.start` and
  `rates.live.stop` read their own line; `new` is the kernel's `entity.create`. Held by `RatesLineCommandTests`,
  `CommandGatewayTests`, `RatesApiTests`, and in the browser by `rates.spec.ts`.
- *As built:* the door reads a command's one output where it has no `answer`, and tells the page what its own
  command answered (`DoorPage.answered`), by which the Rates page opens the Run of `price` and `capture`. The line
  draws a product's terms as the server read them, by their fields (`argumentsShown`). Held by `served.test.ts`.
- *As built:* a line that is exactly a client command's words is that command's (`readLine`), so `new rates` opens
  its dialog beside the served `new`. Held by `commands.test.ts` and `rates.spec.ts`.
- *Changed as built:* `live start` says `Live is running.` whether it started the subscription or found one
  running; the two cannot be told apart without a race. `GET /api/products` and its preview went too: the line's
  reading replaced them.
- *The three defects:* a default is set alone (`set default currency jpy`); a refusal is the server's sentence, on
  the line (`A default notional is more than 0.`), with no request refused and no stack trace; `set`, `reset` and
  `remove` edit and remove a line.
