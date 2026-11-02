// Asset Viewer helpers shared by the archive-kind modules: node payload bytes, names and small encoders.

import { base64ToBytes } from './zed-texture-parser';
import type { NodePayload } from './zipper-workbench-types';

// A ZDB root entry's file name, lower-cased (its path may carry RUN\\...).
export function rootEntryLeaf(e: { name: string; path?: string }): string {
  const full = e.path || e.name;
  return (full.split(/[\\/]/).pop() ?? full).toLowerCase();
}

export function nodeBytes(payload: NodePayload): Uint8Array {
  if (!payload) { return new Uint8Array(0); }
  if (typeof payload === 'string') { return base64ToBytes(payload); }
  // Tag checks instead of instanceof: buffers can be created in another
  // frame's realm on their way into the webview.
  const tag = Object.prototype.toString.call(payload);
  if (tag === '[object ArrayBuffer]') { return new Uint8Array(payload as ArrayBuffer); }
  if (ArrayBuffer.isView(payload)) {
    const view = payload as ArrayBufferView;
    return new Uint8Array(view.buffer, view.byteOffset, view.byteLength);
  }
  // A Node Buffer that went through JSON arrives as { type: 'Buffer', data: [...] }.
  const anyPayload = payload as { data?: unknown; type?: string };
  if (anyPayload && Array.isArray(anyPayload.data)) { return Uint8Array.from(anyPayload.data as number[]); }
  throw new Error(`Unsupported node payload (${tag})`);
}

export function describePayload(payload: NodePayload): string {
  if (payload === null || payload === undefined) { return String(payload); }
  if (typeof payload === 'string') { return `string(${payload.length})`; }
  const tag = Object.prototype.toString.call(payload);
  const keys = typeof payload === 'object' ? Object.keys(payload as object).slice(0, 4).join(',') : '';
  return keys ? `${tag} {${keys}}` : tag;
}

export function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

export function bytesToBase64(bytes: Uint8Array): string {
  let s = '';
  const CHUNK = 0x8000;
  for (let i = 0; i < bytes.length; i += CHUNK) {
    s += String.fromCharCode.apply(null, Array.from(bytes.subarray(i, i + CHUNK)));
  }
  return btoa(s);
}

export function describeMagic(bytes: Uint8Array): string {
  if (bytes.length < 4) { return '(empty)'; }
  return Array.from(bytes.subarray(0, 4))
    .map(b => b.toString(16).padStart(2, '0').toUpperCase())
    .join(' ');
}

export function uniqueLower(values: string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const v of values) {
    const key = v.toLowerCase();
    if (seen.has(key)) { continue; }
    seen.add(key);
    out.push(v);
  }
  return out;
}

// Puts a module's methods on a class's prototype as class methods sit there: non-enumerable, writable, configurable.
export function mixin(target: { prototype: object }, ...parts: object[]): void {
  for (const part of parts) {
    for (const [name, value] of Object.entries(part)) { Object.defineProperty(target.prototype, name, { value, writable: true, configurable: true }); }
  }
}
