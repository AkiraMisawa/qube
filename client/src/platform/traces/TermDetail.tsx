import { useState } from 'react';
import { Alert, Button, Group, Stack, Table, Text } from '@mantine/core';
import type { Side, SideValue, TermKind, TermPage, TermRead, TraceHeader, TraceParameter } from './contracts';
import { checkText, formulaOf, formatNumber, lookupRows, matrixTex, parameterTex, parametersNamed, stateText, symbolTex, valueText } from './formula';
import { Tex } from './Tex';
import { SIDE_TITLE, parameterGroups, parameterSource, providerOf } from './words';

// The selected term (ADR-0011 §6): its formula typeset, first with symbols and then with each priced side's values,
// its Diff, the operand that governed, its reconciliation, a decision's steps and reasons, and its parameters, a
// matrix typeset with the row the lookup took marked. Each operand selects its term.

const LINE_TITLE = { symbolic: 'Formula', before: 'Before', after: 'After', diff: 'Diff' } as const;

export function TermDetail({ header, page, kinds, operand, onSelect }: {
  readonly header: TraceHeader;
  readonly page: TermPage;
  /** The catalogue's kinds, by kind. */
  readonly kinds: ReadonlyMap<string, TermKind>;
  /** A term the formula names, read; undefined while it is being read. */
  readonly operand: (id: string) => TermRead | undefined;
  readonly onSelect: (id: string) => void;
}) {
  const term = page.term;
  const kind = kinds.get(term.kind);
  const sides = header.sides.filter((s) => term[s] !== undefined && term[s] !== null);
  const parameters = { before: page.beforeParameters, after: page.afterParameters };
  const formula = kind === undefined || kind.valueType === 'text' ? undefined : formulaOf(kind, term, sides, operand, parameters);
  const steps = page.children.filter((c) => kinds.get(c.kind)?.valueType === 'text' || sides.some((s) => (c[s]?.because?.length ?? 0) > 0));

  return (
    <Stack gap="xs" className="qwb-trace-detail" data-testid="trace-detail" aria-label={`${term.label}, its formula`}>
      <Group gap="xs" wrap="wrap">
        <Text fw={600} size="sm" data-testid="trace-detail-label">{term.label}</Text>
        <Tex tex={symbolTex(term.symbol)} />
        {term.citation && <Text size="xs" data-testid="trace-detail-citation">{term.citation}</Text>}
      </Group>
      {kind === undefined && (
        <Alert color="red" variant="light" title="Kind not registered">
          The catalogue of {header.method.id} {header.method.version} has no kind {term.kind}, so its formula cannot be shown.
        </Alert>
      )}
      {formula !== undefined && (
        <div className="qwb-trace-formula" data-testid="trace-formula">
          {formula.lines.map((line) => (
            <div key={line.side} className="qwb-trace-formula-line" data-side={line.side}>
              <Text size="xs" c="dimmed" className="qwb-trace-formula-title">{LINE_TITLE[line.side]}</Text>
              <Tex tex={line.tex} display onOperand={(i) => { const o = formula.operands[i]; if (o !== undefined) onSelect(o.termId); }} />
            </div>
          ))}
        </div>
      )}
      {formula !== undefined && formula.operands.length > 0 && (
        <Group gap="xs" wrap="wrap" aria-label="Terms in this formula">
          <Text size="xs" c="dimmed">Terms in this formula</Text>
          {formula.operands.map((o) => {
            const read = operand(o.termId);
            return (
              <Button key={o.termId} size="compact-xs" variant="default" onClick={() => onSelect(o.termId)} data-testid="trace-operand"
                title={read === undefined ? `Open ${o.termId}` : `Open ${read.label}`}>{read?.label ?? o.termId}</Button>
            );
          })}
        </Group>
      )}
      <SideFacts header={header} term={term} sides={sides} operand={operand} />
      {kind?.valueType === 'text' && sides.map((side) => <Decision key={side} side={side} value={term[side]!} both={sides.length === 2} header={header} unit={term.unit} />)}
      {steps.length > 0 && <Steps steps={steps} header={header} onSelect={onSelect} />}
      <Parameters key={term.id} kind={kind} term={term} header={header} parameters={parameters} operand={operand} />
    </Stack>
  );
}

/** What each priced side says of the term besides its value: why it has none, what governed, and its reconciliation. */
function SideFacts({ header, term, sides, operand }: {
  readonly header: TraceHeader; readonly term: TermRead; readonly sides: readonly Side[]; readonly operand: (id: string) => TermRead | undefined;
}) {
  const bySide = sides.map((side) => {
    const value = term[side]!;
    const out: { key: string; text: string; error: boolean }[] = [];
    if (value.state !== 'value') out.push({ key: 'state', text: stateText(value.state, value.reason, providerOf(header, side)), error: value.state !== 'absent' });
    if (value.governed !== undefined && value.governed !== null) {
      const role = typeof value.governed === 'number' ? `operand ${value.governed + 1}` : value.governed;
      const by = typeof value.governed === 'string' ? operand(term.slots?.[value.governed] ?? `${term.id}/${value.governed}`)?.label : undefined;
      out.push({ key: 'governed', text: `governed by ${by === undefined ? role : `${role} (${by})`}`, error: false });
    }
    const check = checkText(value.check, value.value, term.unit);
    if (check !== undefined) out.push({ key: 'check', text: check, error: value.check.status === 'inconsistent' });
    if (value.allocation) out.push({ key: 'allocation', text: `allocation by ${value.allocation.method}: ${formatNumber(value.allocation.value, term.unit)}`, error: false });
    return [side, out] as const;
  });
  // A fact both sides say alike is said once; one that differs is said for each side, named.
  const facts = bySide.flatMap(([side, out]) => out.flatMap((f) => {
    const other = bySide.find(([s]) => s !== side)?.[1].find((o) => o.key === f.key);
    if (sides.length === 2 && other?.text === f.text) return side === sides[0] ? [{ ...f, key: f.key }] : [];
    return [{ ...f, key: `${side}.${f.key}`, text: sides.length === 2 ? `${SIDE_TITLE[side]}: ${f.text}` : f.text }];
  }));
  if (facts.length === 0) return null;
  return (
    <Stack gap={0} data-testid="trace-facts">
      {facts.map((f) => <Text key={f.key} size="xs" {...(f.error ? { c: 'red' } : {})}>{f.text}</Text>)}
    </Stack>
  );
}

/** A decision on one side: its outcome, its reasons, what it read and from where, and the rule it applied. */
function Decision({ side, value, both, header, unit }: {
  readonly side: Side; readonly value: SideValue; readonly both: boolean; readonly header: TraceHeader; readonly unit: TermRead['unit'];
}) {
  const inputs = Object.entries(value.inputs ?? {});
  return (
    <Stack gap={0} className="qwb-trace-decision" data-testid="trace-decision">
      <Text size="sm">{both ? `${SIDE_TITLE[side]}: ` : ''}<b>{valueText(value, unit)}</b>{value.citation ? ` (${value.citation})` : ''}</Text>
      {(value.because ?? []).map((b, i) => <Text key={i} size="xs">{b}</Text>)}
      {inputs.length > 0 && (
        <Table withTableBorder={false} verticalSpacing={2} fz="xs" aria-label="What the decision read">
          <Table.Tbody>
            {inputs.map(([name, input]) => (
              <Table.Tr key={name}>
                <Table.Td>{name}</Table.Td>
                <Table.Td>{input?.value === undefined || input.value === null ? 'none' : String(input.value)}</Table.Td>
                <Table.Td>{[input?.source, input?.where].filter((x) => x).join(' · ')}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      {value.state !== 'value' && <Text size="xs">{stateText(value.state, value.reason, providerOf(header, side))}</Text>}
    </Stack>
  );
}

/** A decision's steps in order (ADR-0011 §6), each with its outcome on every priced side, and a link to open it. */
function Steps({ steps, header, onSelect }: { readonly steps: readonly TermRead[]; readonly header: TraceHeader; readonly onSelect: (id: string) => void }) {
  return (
    <Stack gap="xs" data-testid="trace-steps">
      <Text size="xs" fw={600}>Steps</Text>
      <ol className="qwb-trace-steps">
        {steps.map((step) => (
          <li key={step.id}>
            <Group gap="xs" wrap="wrap">
              <Button size="compact-xs" variant="subtle" onClick={() => onSelect(step.id)}>{step.label}</Button>
              {outcomes(step, header).map((o) => (
                <Text key={o.key} size="xs" {...(o.error ? { c: 'red' } : {})}>{o.text}</Text>
              ))}
            </Group>
            {[...new Set(header.sides.flatMap((side) => step[side]?.because ?? []))].map((b, i) => <Text key={i} size="xs" c="dimmed">{b}</Text>)}
          </li>
        ))}
      </ol>
    </Stack>
  );
}

/** A step's outcome on each priced side, said once where both sides decided alike. */
function outcomes(step: TermRead, header: TraceHeader): readonly { readonly key: string; readonly text: string; readonly error: boolean }[] {
  const each = header.sides.flatMap((side) => {
    const value = step[side];
    if (value === undefined || value === null) return [];
    return [{ side, text: `${valueText(value, step.unit)}${value.citation ? ` (${value.citation})` : ''}`, error: value.state === 'unavailable' || value.state === 'notReturned' }];
  });
  if (each.length === 2 && each[0]!.text === each[1]!.text) return [{ key: 'both', text: each[0]!.text, error: each[0]!.error }];
  return each.map((e) => ({ key: e.side, text: each.length === 2 ? `${SIDE_TITLE[e.side]}: ${e.text}` : e.text, error: e.error }));
}

/**
 * The term's parameters, each side's value where they differ, with their sources; a matrix typeset in a box of its own
 * that scrolls sideways, never the page (§9). First those its formula names and those scoped to it; then, folded, those
 * scoped to a term above it that its formula does not name (a bucket's ρ beside its risk factor). One that holds for the
 * whole trace and that this formula does not name (α beside the risk weight) is the formula's that names it.
 */
function Parameters({ kind, term, header, parameters, operand }: {
  readonly kind: TermKind | undefined;
  readonly term: TermRead;
  readonly header: TraceHeader;
  readonly parameters: Readonly<Record<Side, readonly TraceParameter[] | null | undefined>>;
  readonly operand: (id: string) => TermRead | undefined;
}) {
  const [above, setAbove] = useState(false);
  const named = kind === undefined ? new Set<string>() : parametersNamed(kind);
  const { own, above: inherited } = parameterGroups(named, term.id, header.sides.flatMap((side) => parameters[side] ?? []));
  if (own.length === 0 && inherited.length === 0) return null;
  const marked = Object.fromEntries(header.sides.map((side) => [side, kind === undefined ? {} : lookupRows(kind, term, side, operand)] as const));
  const draw = (name: string) => {
    const bySide = header.sides.map((side) => [side, (parameters[side] ?? []).find((p) => p.name === name)] as const);
    const same = bySide.every(([, p]) => JSON.stringify(p) === JSON.stringify(bySide[0]![1]));
    const shown = same ? [bySide[0]!] : bySide;
    const source = bySide.find(([, p]) => p !== undefined)?.[1];
    return (
      <div key={name} className="qwb-trace-param" data-testid="trace-parameter" data-name={name}>
        <Tex tex={parameterTex(name)} />
        <div className="qwb-trace-param-values">
          {shown.map(([side, p]) => (
            <div key={side}>
              {!same && <Text span size="xs" c="dimmed">{SIDE_TITLE[side]} </Text>}
              {p === undefined ? <Text span size="xs">not in scope</Text>
                : p.matrix ? <div className="qwb-trace-matrix"><Tex tex={matrixTex(p, marked[side]?.[name])} display /></div>
                  : <Text span size="xs" className="qwb-number-text">{p.value !== undefined && p.value !== null ? formatNumber(p.value, p.unit) : p.text ?? ''}</Text>}
            </div>
          ))}
          {source !== undefined && (
            <Text size="xs" c="dimmed" className="qwb-trace-param-source">
              {parameterSource(source)}{source.because?.length ? ` · ${source.because.join(' ')}` : ''}
            </Text>
          )}
        </div>
      </div>
    );
  };
  return (
    <Stack gap="xs" data-testid="trace-parameters" aria-label="Parameters">
      <Text size="xs" fw={600}>Parameters</Text>
      {own.map(draw)}
      {inherited.length > 0 && (
        <>
          <Button size="compact-xs" variant="subtle" className="qwb-trace-param-toggle" aria-expanded={above} onClick={() => setAbove((a) => !a)}
            data-testid="trace-parameters-above">
            {above ? 'Hide' : 'Show'} the parameters of the terms above ({inherited.length})
          </Button>
          {above && inherited.map(draw)}
        </>
      )}
    </Stack>
  );
}
