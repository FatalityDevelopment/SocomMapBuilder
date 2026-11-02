// Asset Viewer: which texture and palette a weapon model draws with (WEAP_MDL payloads name helper atlases too).

// Match a weapon entry name to the right atlas texture filename.
// Strategy (in order):
//   1. Rules table - picks the PRIMARY atlas for known weapons. Important for
//      multi-texture weapons like m4Acarbine (uses both m4.tif for the body
//      and mark03.tif for the scope) where chunk-frequency would pick the
//      wrong one as default.
//   2. Most-frequent chunk-embedded .tif - fallback when rules don't match
//      (less-known weapons / vehicles / etc.).
//   3. Whole-payload ref scan - last resort.
//   Per-chunk overrides are still applied separately by applyModelToRenderer
//   so individual chunks can use their own texture (e.g. the scope chunk
//   uses mark03 even when the default atlas is m4).
export function guessTextureForWeapon(
  weaponName: string,
  sources: Map<string, Uint8Array>,
  model: { chunks: Array<{ textureName: string | null }>; textureRefs: string[] },
): string | null {
  const lower = weaponName.toLowerCase();
  const embedded = chooseEmbeddedWeaponTexture(weaponName, sources, model);
  if (embedded) { return embedded; }

  // Specific matches FIRST so longer / more-specific weapon names win over
  // generic substrings (e.g. "mp5sd" must be checked before "mp5").
  const rules: Array<[string, string]> = [
    // Assault / battle rifles
    ['m4acarbine_203', 'm4.tif'],
    ['m4acarbine_sd', 'm4.tif'],
    ['m4acarbine', 'm4.tif'],
    ['m16_m203', 'm16.tif'],
    ['m16a2', 'm16.tif'],
    ['ak105', 'aks74_s.tif'],
    ['ak47', 'ak47.tif'],
    ['aks74', 'aks74_s.tif'],
    ['m14_gun', 'm14.tif'],
    ['m14', 'm14.tif'],
    ['steyr_aug9', 'steyr.tif'],
    ['steyr', 'steyr.tif'],
    ['iw80a2', 'RA14_groza.tif'],
    ['ra14_groza', 'RA14_groza.tif'],
    ['bmmg_f2k', 'BMMG_f2k.tif'],
    // SMGs
    ['mp5sd', 'mp5.tif'],
    ['mp5', 'mp5.tif'],
    ['fnp90', 'fnp90.tif'],
    ['uzi', 'uzi.tif'],
    ['hk5k', 'mp5.tif'],
    ['sp10_gyuraz', 'SP10_gyuraz.tif'],
    // Sniper rifles
    ['barretm82a1', 'barretm82.tif'],
    ['barret', 'barretm82.tif'],
    ['mcmillanm87', 'mcmillan_m87.tif'],
    ['mcmillan', 'mcmillan_m87.tif'],
    ['stoner_sr25', 'stoner_sr25.tif'],
    ['remington700', 'remington700.tif'],
    ['dragunov', 'drag.tif'],
    // LMGs
    ['stoner_m63a', 'stoner_m63a.tif'],
    ['m60e', 'm60.tif'],
    ['sig_commando', 'sig_commando.tif'],
    // Shotguns
    ['remington870', 'remington870.tif'],
    ['spas12', 'spas12.tif'],
    ['jackhammer', 'jackhammer.tif'],
    // Pistols
    ['m11_p228', 'M11_p228.tif'],
    ['a_mark23sd', 'mark23.tif'],
    ['a_mark23', 'mark23.tif'],
    ['mark23', 'mark23.tif'],
    ['fiveseven', 'fiveseven.tif'],
    ['glock18', 'glock18.tif'],
    ['baretta_m9', 'baretta_m9.tif'],
    ['baretta', 'baretta_m9.tif'],
    ['desert_eagle', 'deserteagle.tif'],
    ['hkp9s', 'p9s.tif'],
    ['sig226', 'sig_p226.tif'],
    // Launchers
    ['m4acarbine_203', 'mark03.tif'],
    ['m203', 'mark03.tif'],
    ['m79', 'm79.tif'],
    ['mglmk1', 'mglmk1.tif'],
    ['mglmk2', 'mglmk2.tif'],
    ['at4_heat', 'at4_text.tif'],
    ['at4', 'at4_text.tif'],
    ['rpg7', 'mp5.tif'],
    ['rpgrenade', 'HEgrenade.tif'],
    // Explosives / mines / utility
    ['pmn_mine', 'pmn_mine.tif'],
    ['pmn', 'pmn_mine.tif'],
    ['c4', 'c401.tif'],
    ['claymore', 'claymore.tif'],
    ['flashbang', 'flashbang.tif'],
    ['hegrenade', 'HEgrenade.tif'],
    ['grenade', 'm79.tif'],
    ['a_smoke_grenade', 'smoke_grenade.tif'],
    ['smoke', 'smoke_grenade.tif'],
    ['detonator', 'a_buttonpanel01.tif'],
    ['fiftycal_box', '50cal_box_top.tif'],
    ['artillery_shell', 'G11b.tif'],
    ['footbomb', 'satchel_tex.tif'],
    ['bleu_chem', 'a_buttonpanel01.tif'],
    // Misc
    ['laser_designator', 'laser_designator.tif'],
  ];

  for (const [needle, tif] of rules) {
    if (lower.includes(needle) && sources.has(tif.toLowerCase())) {
      return tif.toLowerCase();
    }
  }

  // Fallback: most-used chunk-embedded ref.
  const counts = new Map<string, number>();
  for (const c of model.chunks) {
    if (!c.textureName) { continue; }
    const key = c.textureName.toLowerCase();
    if (!sources.has(key)) { continue; }
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  let chunkBest: string | null = null;
  let chunkCount = 0;
  for (const [name, count] of counts) {
    if (count > chunkCount) { chunkCount = count; chunkBest = name; }
  }
  if (chunkBest) { return chunkBest; }

  // Last resort: whole-payload ref scan.
  for (const ref of model.textureRefs ?? []) {
    if (sources.has(ref.toLowerCase())) { return ref.toLowerCase(); }
  }

  return null;
}

export function chooseEmbeddedWeaponTexture(
  weaponName: string,
  sources: Map<string, Uint8Array>,
  model: { chunks: Array<{ textureName: string | null }>; textureRefs: string[] },
): string | null {
  const candidates: string[] = [];
  const add = (name: string | null) => {
    if (!name) { return; }
    const key = name.toLowerCase();
    if (!sources.has(key) || candidates.includes(key)) { return; }
    candidates.push(key);
  };

  for (const ref of model.textureRefs ?? []) { add(ref); }
  for (const chunk of model.chunks) { add(chunk.textureName); }
  if (candidates.length === 0) { return null; }
  if (candidates.length === 1) { return candidates[0]; }

  const weaponKey = compactTextureName(weaponName);
  const direct = candidates.find(c => {
    const texKey = compactTextureName(c.replace(/\.tif$/i, ''));
    return weaponKey.includes(texKey) || texKey.includes(weaponKey);
  });
  if (direct) { return direct; }

  const nonHelper = candidates.filter(c => !isHelperTextureRef(c));
  if (nonHelper.length === 1) { return nonHelper[0]; }
  if (nonHelper.length > 1) { return null; }

  return candidates[candidates.length - 1];
}

export function compactTextureName(value: string): string {
  return value.toLowerCase().replace(/\.tif$/i, '').replace(/[^a-z0-9]+/g, '');
}

export function isHelperTextureRef(value: string): boolean {
  const key = compactTextureName(value);
  return key === 'mark03' ||
    key === 'mark03b' ||
    key === 'handle01' ||
    key === 'laserdesignator' ||
    key === 'smokegrenade02' ||
    key === 'abuttonpanel01';
}

export function isWeaponPrimaryTexture(weaponName: string, tifName: string): boolean {
  const lower = weaponName.toLowerCase();
  const tif = tifName.toLowerCase();
  const primaries: Array<[string, string]> = [
    ['m4acarbine_203', 'm4.tif'],
    ['m4acarbine_sd', 'm4.tif'],
    ['m4acarbine', 'm4.tif'],
    ['m16_m203', 'm16.tif'],
    ['m16a2', 'm16.tif'],
    ['ak105', 'aks74_s.tif'],
    ['ak47', 'ak47.tif'],
    ['aks74', 'aks74_s.tif'],
    ['m14_gun', 'm14.tif'],
    ['m14', 'm14.tif'],
    ['steyr_aug9', 'steyr.tif'],
    ['steyr', 'steyr.tif'],
    ['iw80a2', 'RA14_groza.tif'],
    ['ra14_groza', 'RA14_groza.tif'],
    ['bmmg_f2k', 'BMMG_f2k.tif'],
    ['sig_commando', 'sig_commando.tif'],
    ['mp5sd', 'mp5.tif'],
    ['mp5', 'mp5.tif'],
    ['fnp90', 'fnp90.tif'],
    ['uzi', 'uzi.tif'],
    ['hk5k', 'mp5.tif'],
    ['sp10_gyuraz', 'SP10_gyuraz.tif'],
    ['barretm82a1', 'barretm82.tif'],
    ['barret', 'barretm82.tif'],
    ['mcmillanm87', 'mcmillan_m87.tif'],
    ['mcmillan', 'mcmillan_m87.tif'],
    ['stoner_sr25', 'stoner_sr25.tif'],
    ['remington700', 'remington700.tif'],
    ['dragunov', 'drag.tif'],
    ['stoner_m63a', 'stoner_m63a.tif'],
    ['m60e', 'm60.tif'],
    ['remington870', 'remington870.tif'],
    ['spas12', 'spas12.tif'],
    ['jackhammer', 'jackhammer.tif'],
    ['m11_p228', 'M11_p228.tif'],
    ['a_mark23sd', 'mark23.tif'],
    ['a_mark23', 'mark23.tif'],
    ['mark23', 'mark23.tif'],
    ['fiveseven', 'fiveseven.tif'],
    ['glock18', 'glock18.tif'],
    ['baretta_m9', 'baretta_m9.tif'],
    ['baretta', 'baretta_m9.tif'],
    ['desert_eagle', 'deserteagle.tif'],
    ['hkp9s', 'p9s.tif'],
    ['sig226', 'sig_p226.tif'],
  ];
  return primaries.some(([needle, primary]) => lower.includes(needle) && tif === primary.toLowerCase());
}

export function descriptorTextureForWeapon(weaponName: string, defaultTif: string, descriptorTif: string): string {
  const lower = weaponName.toLowerCase();
  const fallback = defaultTif.toLowerCase();
  const tex = descriptorTif.toLowerCase();

  if (lower.includes('dragunov') && fallback === 'drag.tif') {
    // Dragunov's WEAP_MDL descriptor table names unrelated weapon atlases
    // (barretm82/mp5/jackhammer/etc.) for earlier chunks. The actual model
    // uses drag.tif plus the small DragBar detail page.
    return tex === 'drag.tif' || tex === 'dragbar.tif' ? tex : fallback;
  }

  return tex;
}

export function weaponPrimaryPaletteAlias(weaponName: string, tifName: string): string | null {
  const lower = weaponName.toLowerCase();
  const tif = tifName.toLowerCase();
  for (const rule of WEAP_PALETTE_OVERRIDES) {
    if (!lower.includes(rule.model)) { continue; }
    if (!rule.textures.includes(tif)) { continue; }
    return rule.palette;
  }
  return null;
}

// WEAP_TXR texture header ids are not sufficient for every weapon. Several
// pages are colour variants sharing the same index art; keep confirmed fixes
// here so model binding and palette binding stay separate.
export const WEAP_PALETTE_OVERRIDES: Array<{ model: string; textures: string[]; palette: string }> = [
  { model: 'm4acarbine', textures: ['m4.tif'], palette: 'weap:texpal_4' },
  { model: 'm16', textures: ['m16.tif'], palette: 'weap:texpal_13' },
  { model: 'mp5', textures: ['mp5.tif'], palette: 'weap:texpal_6' },
  { model: 'hk5k', textures: ['mp5.tif'], palette: 'weap:texpal_6' },
  { model: 'ak105', textures: ['aks74_s.tif'], palette: 'weap:texpal_14' },
  { model: 'ak105', textures: ['uzi.tif'], palette: 'weap:texpal_25' },
  { model: 'ak105', textures: ['mcmillan_m87.tif'], palette: 'weap:texpal_32' },
  { model: 'ak105', textures: ['drag.tif'], palette: 'weap:texpal_47' },
  { model: 'ak47', textures: ['ak47.tif'], palette: 'weap:texpal_8' },
  { model: 'aks74', textures: ['aks74_s.tif'], palette: 'weap:texpal_14' },
  { model: 'm14', textures: ['m14.tif'], palette: 'weap:texpal_39' },
  { model: 'steyr', textures: ['steyr.tif'], palette: 'weap:texpal_46' },
  { model: 'bmmg_f2k', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'ra14_groza', textures: ['ra14_groza.tif'], palette: 'weap:texpal_51' },
  { model: 'iw80a2', textures: ['ra14_groza.tif'], palette: 'weap:texpal_51' },
  { model: 'iw80a2', textures: ['spas12.tif'], palette: 'weap:texpal_42' },
  { model: 'sig_commando', textures: ['sig_commando.tif'], palette: 'weap:texpal_23' },
  { model: 'sig_commando', textures: ['m4.tif'], palette: 'weap:texpal_4' },
  { model: 'fnp90', textures: ['fnp90.tif'], palette: 'weap:texpal_24' },
  { model: 'uzi', textures: ['uzi.tif'], palette: 'weap:texpal_25' },
  { model: 'sp10_gyuraz', textures: ['sp10_gyuraz.tif'], palette: 'weap:texpal_50' },
  { model: 'barret', textures: ['barretm82.tif'], palette: 'weap:texpal_30' },
  { model: 'barret', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'barret', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'mcmillan', textures: ['mcmillan_m87.tif'], palette: 'weap:texpal_32' },
  { model: 'mcmillan', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'mcmillan', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'mcmillan', textures: ['mark03b.tif'], palette: 'weap:texpal_20' },
  { model: 'stoner_sr25', textures: ['stoner_sr25.tif'], palette: 'weap:texpal_33' },
  { model: 'stoner_sr25', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'stoner_sr25', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'remington700', textures: ['remington700.tif'], palette: 'weap:texpal_44' },
  { model: 'remington700', textures: ['bmmg_f2k.tif'], palette: 'weap:texpal_31' },
  { model: 'remington700', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'remington700', textures: ['mark03b.tif'], palette: 'weap:texpal_20' },
  { model: 'hegrenade', textures: ['hegrenade.tif'], palette: 'weap:texpal_2' },
  { model: 'rpgrenade', textures: ['hegrenade.tif'], palette: 'weap:texpal_2' },
  { model: 'rpgrenade', textures: ['mglmk2.tif'], palette: 'weap:texpal_37' },
  { model: 'stoner_m63a', textures: ['stoner_m63a.tif'], palette: 'weap:texpal_27' },
  { model: 'm60e', textures: ['m60.tif'], palette: 'weap:texpal_26' },
  { model: 'jackhammer', textures: ['jackhammer.tif'], palette: 'weap:texpal_22' },
  { model: 'm11_p228', textures: ['m11_p228.tif'], palette: 'weap:texpal_49' },
  { model: 'mark23', textures: ['mark23.tif'], palette: 'weap:texpal_10' },
  { model: 'baretta', textures: ['baretta_m9.tif'], palette: 'weap:texpal_11' },
  { model: 'desert_eagle', textures: ['deserteagle.tif'], palette: 'weap:texpal_19' },
  { model: 'glock18', textures: ['glock18.tif'], palette: 'weap:texpal_20' },
  { model: 'hkp9s', textures: ['p9s.tif'], palette: 'weap:texpal_21' },
  { model: 'sig226', textures: ['sig_p226.tif'], palette: 'weap:texpal_18' },
  { model: 'fiveseven', textures: ['fiveseven.tif'], palette: 'weap:texpal_34' },
  { model: 'dragunov', textures: ['drag.tif'], palette: 'weap:texpal_47' },
  { model: 'artillery_shell', textures: ['g11b.tif'], palette: 'weap:texpal_0' },
  { model: 'grenade', textures: ['g11b.tif'], palette: 'weap:texpal_0' },
  { model: 'grenade', textures: ['m79.tif'], palette: 'weap:texpal_1' },
  { model: 'm203', textures: ['m79.tif'], palette: 'weap:texpal_1' },
  { model: 'm79', textures: ['m79.tif'], palette: 'weap:texpal_1' },
  { model: 'mglmk1', textures: ['mglmk1.tif'], palette: 'weap:texpal_36' },
  { model: 'mglmk2', textures: ['mglmk2.tif'], palette: 'weap:texpal_37' },
  { model: 'at4', textures: ['at4_text.tif'], palette: 'weap:texpal_49' },
  { model: 'at4', textures: ['laser_designator.tif'], palette: 'weap:texpal_40' },
  { model: 'at4', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'rpg7', textures: ['barretm82.tif'], palette: 'weap:texpal_30' },
  { model: 'rpg7', textures: ['mp5.tif'], palette: 'weap:texpal_6' },
  { model: 'rpg7', textures: ['jackhammer.tif'], palette: 'weap:texpal_22' },
  { model: 'rpg7', textures: ['stoner_sr25.tif'], palette: 'weap:texpal_33' },
  { model: 'rpg7', textures: ['laser_designator.tif'], palette: 'weap:texpal_40' },
  { model: 'rpg7', textures: ['m79.tif'], palette: 'weap:texpal_1' },
  { model: 'rpg7', textures: ['mcmillan_m87.tif'], palette: 'weap:texpal_32' },
  { model: 'rpg7', textures: ['mglmk1.tif'], palette: 'weap:texpal_36' },
  { model: 'rpg7', textures: ['mark03.tif'], palette: 'weap:texpal_5' },
  { model: 'claymore', textures: ['claymore.tif'], palette: 'weap:texpal_5' },
  { model: 'detonator', textures: ['claymore.tif'], palette: 'weap:texpal_5' },
  { model: 'flashbang', textures: ['flashbang.tif'], palette: 'weap:texpal_49' },
  { model: 'flashbang', textures: ['smoke_grenade02.tif'], palette: 'weap:texpal_18' },
  { model: 'smoke', textures: ['smoke_grenade.tif'], palette: 'weap:texpal_12' },
  { model: 'footbomb', textures: ['satchel_handle.tif', 'satchel_tex.tif'], palette: 'flib:texpal_34' },
  { model: 'bleu_chem', textures: ['sp10_gyuraz.tif', 'a_buttonpanel01.tif'], palette: 'weap:texpal_33' },
  { model: 'fiftycal_box', textures: ['50cal_box_top.tif', '50cal_box_side.tif'], palette: 'weap:texpal_29' },
  { model: 'laser_designator', textures: ['laser_designator.tif'], palette: 'weap:texpal_40' },
];
