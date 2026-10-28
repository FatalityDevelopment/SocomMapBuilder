// Asset Viewer animations: RDR animation clips and .ZANIM archives — the clip preview, its bones and tracks.

import { host } from '../../shared';
import { animationFrameRate, cleanBoneName as cleanAnimationBoneName, hex as formatAnimationHex, parseRdrAnimation, RdrAnimationClip, RdrAnimationTrack, trackTypeKey } from './rdr-animation-parser';
import { RdrAnimationCanvasRenderer } from './rdr-animation-renderer';
import { findZanimAnimationForEntry, parseZanimArchive, ZanimAnimationSummary, ZanimArchiveSummary } from './zanim-parser';
import type { ViewerPageLogic } from './zipper-workbench-page';
import type { BrowserItem } from './zipper-workbench-types';

export const animationMethods = {
  computeZanimSummary(this: ViewerPageLogic): ZanimArchiveSummary | null {
    this.archiveNodesVersion();
    const summary = parseZanimArchive(this.rawEntries(), index => this.nodeCache.get(index));
    return summary.isZanim ? summary : null;
  },

  computeSelectedZanimAnimation(this: ViewerPageLogic): ZanimAnimationSummary | null {
    const entry = this.selectedEntry();
    return findZanimAnimationForEntry(this.zanimSummary(), entry?.index ?? null, entry?.name ?? null);
  },

  computeVisibleZanimAnimations(this: ViewerPageLogic): ZanimAnimationSummary[] {
    const summary = this.zanimSummary();
    if (!summary) { return []; }
    const q = this.search().trim().toLowerCase();
    const animations = summary.sets.flatMap(set => set.animations);
    if (!q) { return animations; }
    return animations.filter(anim =>
      [anim.name, ...anim.nameRefs].join(' ').toLowerCase().includes(q));
  },

  computeAnimationBoneRows(this: ViewerPageLogic): Array<{ index: number; name: string; clean: string; parent: number; trackType: string; attachment: boolean; depth: number }> {
    const clip = this.animationClip();
    if (!clip) { return []; }
    return clip.bones.map((name, index) => {
      const clean = cleanAnimationBoneName(name);
      const track = clip.tracks[index];
      const depth = ((name || '').match(/^-*/) || [''])[0].length;
      return {
        index,
        name,
        clean,
        parent: clip.parents[index] ?? -1,
        trackType: track ? trackTypeKey(track.type) : '-',
        attachment: /weapon|rifle|gun|pistol|cig|cigar|smoke|prop|attach/i.test(clean),
        depth,
      };
    });
  },

  openInAnimationEditor(this: ViewerPageLogic): void { host.open('animations', this.rootPath()); },

  isAnimationArchive(this: ViewerPageLogic, item: BrowserItem): boolean {
    return item.kind === 'archive' && /(MOTION[^\\/]*|ZANIM)\.ZAR$/i.test(item.name);
  },

  selectZanimAnimation(this: ViewerPageLogic, entryIndex: number): void {
    this.selectedIndex.set(entryIndex);
  },

  zanimWordsLabel(this: ViewerPageLogic, words: number[] | undefined): string {
    if (!words || words.length === 0) { return '-'; }
    return words.map(w => '0x' + (w >>> 0).toString(16).toUpperCase()).join(' ');
  },

  zanimParamsLabel(this: ViewerPageLogic, params: { words: number[]; floats: number[] } | null): string {
    if (!params) { return 'loading...'; }
    const words = params.words.map(w => '0x' + (w >>> 0).toString(16).toUpperCase()).join(' ');
    const floats = params.floats.map(f => Number.isFinite(f) ? f.toFixed(3) : 'nan').join(', ');
    return `${words}  | f32 ${floats}`;
  },

  zanimNodeRefLabel(this: ViewerPageLogic, words: [number, number, number, number]): string {
    return `[${words.map(w => '0x' + w.toString(16).toUpperCase()).join(', ')}]`;
  },

  toggleAnimationPlayback(this: ViewerPageLogic): void {
    const next = !this.animationPlaying();
    this.animationPlaying.set(next);
    this.animationRenderer?.setPlaying(next);
  },

  resetAnimationView(this: ViewerPageLogic): void {
    this.animationRenderer?.resetView();
  },

  onAnimationFrameInput(this: ViewerPageLogic, event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    if (!Number.isFinite(value)) { return; }
    this.animationPlaying.set(false);
    this.animationRenderer?.setPlaying(false);
    this.animationRenderer?.setFrame(value);
    this.animationFrame.set(Math.floor(value));
  },

  animationFrameMax(this: ViewerPageLogic, clip: RdrAnimationClip): number {
    return Math.max(0, clip.sampleCount - 1);
  },

  animationFpsLabel(this: ViewerPageLogic, clip: RdrAnimationClip): string {
    return `${animationFrameRate(clip).toFixed(2)} fps`;
  },

  cleanAnimationBoneLabel(this: ViewerPageLogic, name: string): string {
    return cleanAnimationBoneName(name);
  },

  isAnimationAttachment(this: ViewerPageLogic, name: string): boolean {
    return /weapon|rifle|gun|pistol|cig|cigar|smoke|prop|attach/i.test(cleanAnimationBoneName(name));
  },

  animationHex(this: ViewerPageLogic, value: number): string {
    return formatAnimationHex(value);
  },

  animationTrackBase(this: ViewerPageLogic, track: RdrAnimationTrack): string {
    if (track.baseVec4) { return `[${track.baseVec4.join(', ')}]`; }
    if (track.posSamples?.length) {
      const p = track.posSamples[0];
      return `[${p.map(v => Math.round(v)).join(', ')}] -> animated`;
    }
    return 'n/a';
  },

  animationTrackQuat(this: ViewerPageLogic, track: RdrAnimationTrack): string {
    if (track.staticQuat) { return `[${track.staticQuat.join(', ')}] static`; }
    if (track.quatSamples?.length) { return `[${track.quatSamples[0].join(', ')}] -> animated`; }
    return 'n/a';
  },

  tryApplyAnimationPreview(this: ViewerPageLogic, bytes: Uint8Array, name: string): boolean {
    const leaf = name.split(/[\\/]/).pop() ?? name;
    const animation = parseRdrAnimation(bytes, leaf);
    if (!animation.animationLike) { return false; }

    this.previewMode.set('animation');
    this.animationClip.set(animation);
    this.animationPlaying.set(true);
    this.animationFrame.set(0);
    this.previewMessage.set(animation.parseOk
      ? ''
      : `Animation parsed partially (${animation.tracks.length}/${animation.boneCount} tracks).`);
    this.log(
      `[ANIM] ${leaf}: ${animation.boneCount} bones, ${animation.sampleCount} samples, ` +
      `${animation.tracks.length} tracks, ${animation.parseOk ? 'clean' : 'partial'}`,
    );
    setTimeout(() => this.applyAnimationToRenderer(), 0);
    return true;
  },

  ensureAnimationRenderer(this: ViewerPageLogic): void {
    const canvas = this.animationCanvas?.nativeElement;
    if (!canvas) { return; }
    if (this.animationRenderer && this.animationRenderer.attachedCanvas !== canvas) {
      this.animationRenderer.dispose();
      this.animationRenderer = null;
    }
    if (!this.animationRenderer) {
      this.animationRenderer = new RdrAnimationCanvasRenderer(canvas);
      this.animationRenderer.onFrameChange = frame => this.animationFrame.set(frame);
    }
  },

  applyAnimationToRenderer(this: ViewerPageLogic): void {
    this.ensureAnimationRenderer();
    if (!this.animationRenderer) { return; }
    this.animationRenderer.setClip(this.animationClip());
    this.animationRenderer.setPlaying(this.animationPlaying());
  },

  requestArchiveNodesIfNeeded(this: ViewerPageLogic): void {
    const filePath = this.result()?.path;
    if (!filePath || this.archiveNodesRequestedFor === filePath) { return; }
    if (!this.zanimSummary()) { return; }
    this.archiveNodesRequestedFor = filePath;
    this.setStatus(`Loading ${filePath.split(/[\\/]/).pop()} animation payloads...`, false, true);
    this.vscode.postMessage({ type: 'loadArchiveNodes', path: filePath });
  },

  selectInitialZanimEntryIfNeeded(this: ViewerPageLogic): void {
    if (this.selectedIndex() !== null) { return; }
    const summary = this.zanimSummary();
    if (!summary) { return; }
    const firstAnimation = summary.sets[0]?.animations[0];
    const animSets = summary.entries.find(e => e.parent < 0 && e.name.toLowerCase() === 'anim_sets');
    this.selectedIndex.set(firstAnimation?.entryIndex ?? animSets?.index ?? null);
  },
};

export type AnimationMethods = typeof animationMethods;
