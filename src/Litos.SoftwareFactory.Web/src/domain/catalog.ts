import type { CatalogModel, OfferedProvider } from '../api/types';

/**
 * A model's family, for the catalog picker's filter: OpenRouter's vendor ("anthropic" in
 * "anthropic/claude-sonnet-5"), otherwise the id's first word ("claude", "gpt", "gemini").
 */
export function modelFamily(id: string): string {
  const slash = id.indexOf('/');
  if (slash > 0) return id.slice(0, slash);
  return id.split(/[-_.:\s]/)[0] || id;
}

/** The families in a catalog, each once, alphabetically. */
export function families(models: CatalogModel[]): string[] {
  return [...new Set(models.map((m) => modelFamily(m.id)))].sort();
}

export interface CatalogFilter {
  /** Matched against the id and the display name, ignoring case; every word must match. */
  search: string;
  /** Leave out models the provider says cannot take tools. One that does not say is kept. */
  toolsOnly: boolean;
  /** Leave out models with a smaller, or unknown, context length; 0 for any. */
  minContext: number;
  /** '' for every family. */
  family: string;
  /** Ids to leave out: those allowed already, when "hide allowed" is on. */
  hide: ReadonlySet<string>;
}

export const NO_FILTER: CatalogFilter = { search: '', toolsOnly: false, minContext: 0, family: '', hide: new Set() };

export function filterCatalog(models: CatalogModel[], filter: CatalogFilter): CatalogModel[] {
  const words = filter.search.toLowerCase().split(/\s+/).filter(Boolean);
  return models.filter((m) => {
    if (filter.hide.has(m.id)) return false;
    if (filter.toolsOnly && m.supportsTools === false) return false;
    if (filter.minContext > 0 && (m.contextLength ?? 0) < filter.minContext) return false;
    if (filter.family && modelFamily(m.id) !== filter.family) return false;
    const text = `${m.id} ${m.displayName ?? ''}`.toLowerCase();
    return words.every((w) => text.includes(w));
  });
}

/** "$3.00 / $15.00" per million input and output tokens; "" when the provider reports no price. */
export function priceText(model: CatalogModel): string {
  if (model.inputPricePerMillion === null || model.outputPricePerMillion === null) return '';
  const dollars = (n: number) => (n === 0 ? 'free' : `$${n < 1 ? n.toFixed(3) : n.toFixed(2)}`);
  return `${dollars(model.inputPricePerMillion)} / ${dollars(model.outputPricePerMillion)}`;
}

/** One model a person can choose for a new thread. */
export interface ModelChoice {
  provider: OfferedProvider;
  id: string;
  contextLength: number;
}

/** The value a choice is selected by: provider and model, which a model id alone is not unique across. */
export const choiceKey = (provider: string, model: string) => `${provider}\u0000${model}`;

/**
 * What New thread pins above the providers' lists (m3-architecture.md §4.4): the factory's
 * default first, then this person's recent models, each once and only if still offered.
 */
export function pinnedChoices(
  offered: OfferedProvider[],
  byDefault: { provider: string | null; model: string | null },
  recent: { provider: string; model: string }[],
): ModelChoice[] {
  const pinned: ModelChoice[] = [];
  const add = (providerName: string | null, model: string | null) => {
    const provider = offered.find((p) => p.name === providerName);
    const found = provider?.models.find((m) => m.id === model);
    if (!provider || !found || pinned.some((c) => c.provider.name === provider.name && c.id === found.id)) return;
    pinned.push({ provider, id: found.id, contextLength: found.contextLength });
  };
  add(byDefault.provider, byDefault.model);
  for (const r of recent) add(r.provider, r.model);
  return pinned;
}
