// Runs a ZANIM effect script (ASSET_WEAPON_FX / ASSET_EFFECT tracks) the way the game's interpreter does, for the
// preview: tracks run in parallel; IF / ELSEIF / ELSE / ENDIF with RANDOM_WEIGHT conditions, WAIT (frames at 30 fps
// or seconds), LOOP, STOP_SEQUENCE, OBJECT_ACTIVE_STATE, OBJECT_ROTATE_STATE / OBJECT_TRANSLATE_STATE (Euler / copy
// from the owner), OBJECT_MOTION_FROM_TO scale curves, LIGHT and CALL_ANIMATION. Opcodes it does not model
// (particles, sounds, physics) are skipped and listed in `skipped`. The viewport draws `objects` and `lights`.

import type { FxInstruction, FxNode } from '../model-editor/asset-client';

export interface FxObjectState {
  visible: boolean;
  // Radians, applied X then Y then Z.
  euler: [number, number, number];
  scale: [number, number, number];
  followOwner: boolean;
}

export interface FxLight { color: [number, number, number]; range: number; age: number; life: number }

const FRAME = 1 / 30;

interface Motion { object: string; elapsed: number; duration: number; end: [number, number, number]; rate: [number, number, number] }

interface TrackRun {
  target: string;
  ins: FxInstruction[];
  pc: number;
  wait: number;
  loops: number;
  motion: Motion | null;
  done: boolean;
  waitingOn: FxInstance | null;
  // Set while jumping to the next ELSEIF / ELSE after a false condition (as opposed to falling into it).
  entering: boolean;
}

export class FxInstance {
  readonly objects = new Map<string, FxObjectState>();
  readonly lights: FxLight[] = [];
  readonly children: FxInstance[] = [];
  readonly skipped = new Set<string>();
  private readonly tracks: TrackRun[];
  age = 0;

  constructor(readonly node: FxNode, private readonly resolve: (name: string) => FxNode | null, readonly depth = 0) {
    this.tracks = (node.tracks ?? []).map(t => ({
      target: t.target, ins: t.instructions, pc: 0, wait: 0, loops: 0, motion: null, done: t.instructions.length === 0, waitingOn: null, entering: false,
    }));
  }

  get done(): boolean { return this.tracks.every(t => t.done) && this.children.every(c => c.done) && this.lights.length === 0; }

  object(name: string): FxObjectState {
    let o = this.objects.get(name);
    if (!o) { o = { visible: false, euler: [0, 0, 0], scale: [1, 1, 1], followOwner: false }; this.objects.set(name, o); }
    return o;
  }

  step(dt: number): void {
    this.age += dt;
    for (const t of this.tracks) { this.runTrack(t, dt); }
    for (const l of this.lights) { l.age += dt; }
    for (let i = this.lights.length - 1; i >= 0; i--) { if (this.lights[i].age >= this.lights[i].life) { this.lights.splice(i, 1); } }
    for (const c of this.children) { c.step(dt); }
  }

  private runTrack(t: TrackRun, dt: number): void {
    if (t.done) { return; }
    if (t.wait > 0) { t.wait -= dt; if (t.wait > 0) { return; } }
    if (t.motion) {
      const m = t.motion, o = this.object(m.object);
      m.elapsed += dt;
      if (m.elapsed >= m.duration) { o.scale = [...m.end]; t.motion = null; }
      else { o.scale = o.scale.map((v, k) => v + m.rate[k] * dt) as [number, number, number]; return; }
    }
    if (t.waitingOn && !t.waitingOn.done) { return; }
    t.waitingOn = null;
    // A frame's worth of instructions: stop at a WAIT, a running motion, a LOOP back-jump or the end.
    for (let guard = 0; guard < 512; guard++) {
      if (t.pc >= t.ins.length) { t.done = true; return; }
      const i = t.ins[t.pc];
      const f = i.fields ?? {};
      switch (i.name) {
        case 'IF':
        case 'ELSEIF': {
          // Reached in order: an ELSEIF ends the branch just run; skip to ENDIF.
          if (i.name === 'ELSEIF' && !t.entering) { t.pc = this.matchEnd(t.ins, t.pc); continue; }
          t.entering = false;
          const { ok, next } = this.condition(t.ins, t.pc);
          t.pc = ok ? next : this.nextBranch(t.ins, t.pc);
          if (!ok) { t.entering = true; }
          continue;
        }
        case 'ELSE':
          if (!t.entering) { t.pc = this.matchEnd(t.ins, t.pc); continue; }
          t.entering = false;
          t.pc++;
          continue;
        case 'ENDIF':
          t.entering = false;
          t.pc++;
          continue;
        case 'WAIT': {
          const seconds = typeof f['frames'] === 'number' ? (f['frames'] as number) * FRAME : Number(f['seconds'] ?? 0);
          const random = typeof f['random'] === 'number' ? Math.random() * (f['random'] as number) * (typeof f['frames'] === 'number' ? FRAME : 1) : 0;
          t.pc++;
          t.wait = Math.max(FRAME / 2, seconds + random);
          return;
        }
        case 'LOOP': {
          const count = typeof f['count'] === 'number' ? (f['count'] as number) : -1;
          t.loops++;
          if (count < 0 || t.loops < count) { t.pc = 0; return; }
          t.pc++;
          continue;
        }
        case 'STOP_SEQUENCE':
          // The flash scripts stop their looping fix-up track this way before hiding the flash.
          for (const other of this.tracks) { if (other !== t) { other.done = true; } }
          t.pc++;
          continue;
        case 'OBJECT_ACTIVE_STATE':
          this.object(String(f['object'] ?? 'self')).visible = !!f['active'];
          t.pc++;
          continue;
        case 'OBJECT_ROTATE_STATE':
        case 'OBJECT_TRANSLATE_STATE': {
          const o = this.object(String(f['object'] ?? 'self'));
          if (f['reference'] === 'owner') { o.followOwner = true; }
          const flags = Number(f['flags'] ?? 0);
          if (i.name === 'OBJECT_ROTATE_STATE') {
            const e = (f['euler'] as number[] | undefined) ?? [0, 0, 0];
            if (flags & 1) { o.euler = [e[0], e[1], e[2]]; }
            else if (flags & 2) { o.euler = [o.euler[0] + e[0], o.euler[1] + e[1], o.euler[2] + e[2]]; }
          }
          t.pc++;
          continue;
        }
        case 'OBJECT_MOTION_FROM_TO': {
          t.pc++;
          const object = String(f['object'] ?? 'self');
          const start = f['scaleStart'] as number[] | undefined, end = f['scaleEnd'] as number[] | undefined, rate = f['scaleRate'] as number[] | undefined;
          if (start && end && rate) {
            this.object(object).scale = [start[0], start[1], start[2]];
            t.motion = { object, elapsed: 0, duration: Number(f['duration'] ?? 0), end: [end[0], end[1], end[2]], rate: [rate[0], rate[1], rate[2]] };
            return;
          }
          this.skipped.add('OBJECT_MOTION_FROM_TO (path)');
          continue;
        }
        case 'LIGHT': {
          const c = (f['color'] as number[] | undefined) ?? [255, 255, 255];
          const time = Number(f['time'] ?? 0);
          this.lights.push({ color: [c[0] / 255, c[1] / 255, c[2] / 255], range: Number(f['range'] ?? 20), age: 0, life: time > 0 && time < 5 ? time : 0.07 });
          t.pc++;
          continue;
        }
        case 'CALL_ANIMATION': {
          t.pc++;
          const child = this.depth < 4 ? this.resolve(String(f['animation'] ?? '')) : null;
          if (child) {
            const inst = new FxInstance(child, this.resolve, this.depth + 1);
            this.children.push(inst);
            if (f['waitForEnd']) { t.waitingOn = inst; return; }
          }
          continue;
        }
        case 'QUAD_ALIGN':
        case 'RANDOM_WEIGHT':
          t.pc++;
          continue;
        default:
          this.skipped.add(i.name);
          t.pc++;
          continue;
      }
    }
  }

  /** Evaluates the condition instructions after an IF / ELSEIF: all must hold (RANDOM_WEIGHT; others are false). */
  private condition(ins: FxInstruction[], at: number): { ok: boolean; next: number } {
    let p = at + 1, ok = true, any = false;
    while (p < ins.length && ins[p].condition) {
      const c = ins[p];
      any = true;
      if (c.name === 'RANDOM_WEIGHT') { ok = ok && Math.random() <= Number(c.fields?.['weight'] ?? 0); }
      else { ok = false; this.skipped.add(`condition ${c.name}`); }
      p++;
    }
    return { ok: any ? ok : true, next: p };
  }

  /** The next ELSEIF / ELSE / ENDIF at the same depth as the IF at `at`. */
  private nextBranch(ins: FxInstruction[], at: number): number {
    const d = ins[at].depth;
    for (let p = at + 1; p < ins.length; p++) {
      if (ins[p].depth === d && !ins[p].condition && ['ELSEIF', 'ELSE', 'ENDIF'].includes(ins[p].name)) { return p; }
    }
    return ins.length;
  }

  /** The ENDIF closing the chain that the ELSEIF / ELSE at `at` belongs to. */
  private matchEnd(ins: FxInstruction[], at: number): number {
    const d = ins[at].depth;
    for (let p = at + 1; p < ins.length; p++) {
      if (ins[p].depth === d && !ins[p].condition && ins[p].name === 'ENDIF') { return p + 1; }
    }
    return ins.length;
  }

  /** Every instance in this tree (this one first). */
  all(): FxInstance[] { return [this, ...this.children.flatMap(c => c.all())]; }
}
