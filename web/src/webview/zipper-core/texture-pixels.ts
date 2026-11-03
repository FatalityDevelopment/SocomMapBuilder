// A decoded texture as the viewports draw it: top-down RGBA plus the GS register state that decides how the game
// samples and blends it.
export interface ModelTexturePixels {
  width: number;
  height: number;
  rgba: Uint8Array;
  // From the texture's GS CLAMP register: per-axis wrap mode.
  wrapS?: 'repeat' | 'clamp';
  wrapT?: 'repeat' | 'clamp';
  // From the GS TEST register: discard texels below this alpha (0..1).
  alphaTest?: number | null;
  // True when the decoded pixels contain any non-opaque texel.
  hasTransparency?: boolean;
  // Fraction of texels with alpha below 50% (0..1).
  transparentRatio?: number;
  // From the GS ALPHA register: the texture is alpha blended in game.
  alphaBlend?: boolean;
}
