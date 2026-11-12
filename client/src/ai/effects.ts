import type { ComponentType } from 'react';
import type { QueryClient } from '@tanstack/react-query';
import type { Context } from '@ag-ui/core';
import type { Panel, WorkspaceState } from '../platform/workspace/workspaceStore';

// What an app or the shell registers with the AI pane, so the pane knows no domain: the context its panel gives the
// agent (ADR-0007 §8), what the pane shows once one of its commands has done something (§7), and how a card draws an
// answer that is more than its JSON (ADR-0018 §8: a proposal's plan). A result may open a panel behind the active one;
// it never activates a panel or moves the keyboard.

export interface AiEffectTarget {
  readonly workspace: Pick<WorkspaceState, 'openPanelBehind' | 'panels'>;
  readonly queryClient: QueryClient;
  /** The tool call that answered, which its card names it by. */
  readonly call: string;
}

/** What a done command's outputs change on screen. Answers the panel it opened, if any. */
export type AiEffect = (outputs: Readonly<Record<string, unknown>>, target: AiEffectTarget) => Panel | undefined;

/** How a card draws a done command's answer, in place of its JSON: the answer, and the tool call that gave it. */
export type AiAnswerView = ComponentType<{ readonly outputs: Readonly<Record<string, unknown>>; readonly call: string }>;

const effects = new Map<string, AiEffect>();
const answers = new Map<string, AiAnswerView>();
const contexts = new Map<string, (panel: Extract<Panel, { kind: 'app' }>) => Context | undefined>();
const shared = new Set<() => Context>();

// Registered when an app's module loads; loading it again (a hot reload) replaces what it registered.
export function registerAiEffect(capabilityId: string, effect: AiEffect): void {
  effects.set(capabilityId, effect);
}

export function aiEffectOf(capabilityId: string): AiEffect | undefined {
  return effects.get(capabilityId);
}

export function registerAiAnswer(capabilityId: string, view: AiAnswerView): void {
  answers.set(capabilityId, view);
}

export function aiAnswerOf(capabilityId: string): AiAnswerView | undefined {
  return answers.get(capabilityId);
}

export function registerAiContext(app: string, context: (panel: Extract<Panel, { kind: 'app' }>) => Context | undefined): void {
  contexts.set(app, context);
}

/**
 * Registers context that every request carries, whichever panel is active, such as the shell's open panels and their
 * arrangement (ADR-0018 §8); answers what takes it back.
 */
export function registerAiSharedContext(context: () => Context): () => void {
  shared.add(context);
  return () => { shared.delete(context); };
}

/** The context a request carries: what the active panel's app registered, then what every request carries. */
export function aiContextOf(panel: Panel | undefined): Context[] {
  const entry = panel?.kind === 'app' ? contexts.get(panel.app)?.(panel) : undefined;
  return [...(entry === undefined ? [] : [entry]), ...[...shared].map((context) => context())];
}
