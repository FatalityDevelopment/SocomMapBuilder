// The page's methods come from one module per archive kind: each must land on the prototype as a class method would
// (non-enumerable), and no module may override another's.

import { describe, expect, it } from 'vitest';
import { ViewerPageLogic } from './zipper-workbench-page';
import { aiMapMethods } from './zipper-workbench-aimaps';
import { animationMethods } from './zipper-workbench-animations';
import { browserMethods } from './zipper-workbench-browser';
import { clutterMethods } from './zipper-workbench-clutter';
import { fileMethods } from './zipper-workbench-files';
import { geoMethods } from './zipper-workbench-geo';
import { messageMethods } from './zipper-workbench-messages';
import { modelComposeMethods } from './zipper-workbench-model-compose';
import { modelRenderMethods } from './zipper-workbench-model-render';
import { modelMethods } from './zipper-workbench-models';
import { packageMethods } from './zipper-workbench-package';
import { paletteMethods } from './zipper-workbench-palettes';
import { rdrMethods } from './zipper-workbench-rdr';
import { textureMethods } from './zipper-workbench-textures';

const parts: Record<string, object> = {
  aiMapMethods, animationMethods, browserMethods, clutterMethods, fileMethods, geoMethods, messageMethods, modelComposeMethods,
  modelRenderMethods, modelMethods, packageMethods, paletteMethods, rdrMethods, textureMethods,
};

describe('ViewerPageLogic methods', () => {
  it('puts every module method on the prototype, non-enumerable, once', () => {
    const wrong: string[] = [];
    for (const [part, methods] of Object.entries(parts)) {
      for (const [name, fn] of Object.entries(methods)) {
        const d = Object.getOwnPropertyDescriptor(ViewerPageLogic.prototype, name);
        if (!d || d.value !== fn || d.enumerable) { wrong.push(`${part}.${name}`); }
      }
    }
    expect(wrong).toEqual([]);
    expect(Object.keys(ViewerPageLogic.prototype)).toEqual([]);
  });
});
