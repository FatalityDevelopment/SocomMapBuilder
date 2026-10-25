// A small stand-in for Angular's signal(), computed(), effect() and untracked(), so the editor pages' logic moves
// over from CodeDesigner unchanged, with the same semantics:
// - a computed tracks the signals / computeds it read and recomputes only when one of them changed; when the new value
//   is the same (Object.is) it keeps its identity, so dependants do not see a change;
// - an effect re-runs (batched, in a microtask) only when something it read changed; untracked() reads without
//   subscribing.
// React components subscribe with useSignals() and re-render after any change.

import { useSyncExternalStore } from 'react';

interface Node { version: number }

interface ComputedNode<T> extends Node {
  kind: 'computed';
  fn: () => T;
  value: T | undefined;
  initialized: boolean;
  deps: Map<Node, number>;
  checkedEpoch: number;
}

interface Consumer { deps: Map<Node, number> }

// Bumped by every write; lets an up-to-date computed skip re-checking its dependencies.
let epoch = 0;
let active: Consumer | null = null;
let scheduled = false;
const listeners = new Set<() => void>();
const effects = new Set<() => void>();

function track(node: Node): void {
  if (active) { active.deps.set(node, node.version); }
}

function isComputed(node: Node): node is ComputedNode<unknown> {
  return (node as ComputedNode<unknown>).kind === 'computed';
}

// True when any dependency changed since it was read.
function stale(deps: Map<Node, number>): boolean {
  for (const [dep, seen] of deps) {
    if (isComputed(dep)) { refresh(dep); }
    if (dep.version !== seen) { return true; }
  }
  return false;
}

function refresh<T>(node: ComputedNode<T>): void {
  if (node.initialized && node.checkedEpoch === epoch) { return; }
  if (node.initialized && !stale(node.deps)) { node.checkedEpoch = epoch; return; }
  const previous = active;
  const consumer: Consumer = { deps: new Map() };
  active = consumer;
  try {
    const value = node.fn();
    if (!node.initialized || !Object.is(value, node.value)) {
      node.value = value;
      node.version++;
    }
    node.initialized = true;
  } finally {
    active = previous;
    node.deps = consumer.deps;
    node.checkedEpoch = epoch;
  }
}

function changed(): void {
  epoch++;
  if (scheduled) { return; }
  scheduled = true;
  queueMicrotask(() => {
    scheduled = false;
    for (const run of [...effects]) { run(); }
    for (const l of [...listeners]) { l(); }
  });
}

export interface Signal<T> { (): T }

export interface WritableSignal<T> extends Signal<T> {
  set(value: T): void;
  update(fn: (value: T) => T): void;
  asReadonly(): Signal<T>;
}

export function signal<T>(initial: T): WritableSignal<T> {
  const node: Node = { version: 0 };
  let value = initial;
  const read = (() => { track(node); return value; }) as WritableSignal<T>;
  read.set = next => {
    if (Object.is(next, value)) { return; }
    value = next;
    node.version++;
    changed();
  };
  read.update = fn => read.set(fn(value));
  read.asReadonly = () => read;
  return read;
}

export function computed<T>(fn: () => T): Signal<T> {
  const node: ComputedNode<T> = { kind: 'computed', fn, value: undefined, initialized: false, deps: new Map(), checkedEpoch: -1, version: 0 };
  return () => {
    refresh(node);
    track(node);
    return node.value as T;
  };
}

export interface EffectRef { destroy(): void }

// Runs fn now, and again (coalesced per microtask) whenever a signal or computed it read changes, until destroyed.
export function effect(fn: () => void): EffectRef {
  let consumer: Consumer = { deps: new Map() };
  const execute = () => {
    const previous = active;
    const next: Consumer = { deps: new Map() };
    active = next;
    try { fn(); } finally { active = previous; consumer = next; }
  };
  const check = () => {
    const previous = active;
    active = null;
    try { if (!stale(consumer.deps)) { return; } } finally { active = previous; }
    execute();
  };
  effects.add(check);
  execute();
  return { destroy: () => effects.delete(check) };
}

export function untracked<T>(fn: () => T): T {
  const previous = active;
  active = null;
  try { return fn(); } finally { active = previous; }
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

// Re-renders the calling component whenever any signal changes.
export function useSignals(): number {
  return useSyncExternalStore(subscribe, () => epoch);
}
