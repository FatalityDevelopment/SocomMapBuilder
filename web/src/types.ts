// Decoded texture pixels as the viewport uploads them (top-down RGBA).
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
  alphaBlend?: boolean | null;
}
