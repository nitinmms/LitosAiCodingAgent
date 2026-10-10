import { describe, expect, it } from 'vitest';
import type { CatalogModel, OfferedProvider } from '../api/types';
import { families, filterCatalog, modelFamily, NO_FILTER, pinnedChoices, priceText } from './catalog';

const model = (id: string, more: Partial<CatalogModel> = {}): CatalogModel => ({
  id,
  displayName: null,
  contextLength: 128_000,
  supportsTools: null,
  inputPricePerMillion: null,
  outputPricePerMillion: null,
  ...more,
});

describe('a model’s family', () => {
  it('is OpenRouter’s vendor, or the first word of a native id', () => {
    expect(modelFamily('anthropic/claude-sonnet-5')).toBe('anthropic');
    expect(modelFamily('claude-sonnet-5')).toBe('claude');
    expect(modelFamily('gpt-6-luna')).toBe('gpt');
    expect(modelFamily('gemini-3-pro')).toBe('gemini');
    expect(modelFamily('o5')).toBe('o5');
    expect(modelFamily('qwen3:32b')).toBe('qwen3');
  });

  it('lists each family once, in order', () => {
    expect(families([model('openai/gpt-6'), model('anthropic/claude-sonnet-5'), model('openai/o5')])).toEqual(['anthropic', 'openai']);
  });
});

describe('filtering a catalog', () => {
  const catalog = [
    model('anthropic/claude-sonnet-5', { displayName: 'Anthropic: Claude Sonnet 5', contextLength: 200_000, supportsTools: true }),
    model('some/chat-only', { contextLength: 32_000, supportsTools: false }),
    model('some/unreported', { contextLength: 64_000 }),
    model('some/unknown-window', { contextLength: null, supportsTools: true }),
  ];
  const ids = (models: CatalogModel[]) => models.map((m) => m.id);

  it('keeps everything with no filter', () => {
    expect(filterCatalog(catalog, NO_FILTER)).toEqual(catalog);
  });

  it('leaves out only models said not to take tools', () => {
    expect(ids(filterCatalog(catalog, { ...NO_FILTER, toolsOnly: true }))).toEqual(['anthropic/claude-sonnet-5', 'some/unreported', 'some/unknown-window']);
  });

  it('leaves out smaller and unknown context lengths', () => {
    expect(ids(filterCatalog(catalog, { ...NO_FILTER, minContext: 64_000 }))).toEqual(['anthropic/claude-sonnet-5', 'some/unreported']);
  });

  it('matches every word of a search against the id and the display name', () => {
    expect(ids(filterCatalog(catalog, { ...NO_FILTER, search: 'CLAUDE 5' }))).toEqual(['anthropic/claude-sonnet-5']);
    expect(ids(filterCatalog(catalog, { ...NO_FILTER, search: 'anthropic: sonnet' }))).toEqual(['anthropic/claude-sonnet-5']);
    expect(filterCatalog(catalog, { ...NO_FILTER, search: 'claude opus' })).toEqual([]);
  });

  it('narrows to a family, and hides the ids given', () => {
    expect(ids(filterCatalog(catalog, { ...NO_FILTER, family: 'some', hide: new Set(['some/chat-only']) }))).toEqual([
      'some/unreported',
      'some/unknown-window',
    ]);
  });
});

describe('a model’s price', () => {
  it('is per million input and output tokens, when reported', () => {
    expect(priceText(model('a', { inputPricePerMillion: 3, outputPricePerMillion: 15 }))).toBe('$3.00 / $15.00');
    expect(priceText(model('a', { inputPricePerMillion: 0.2, outputPricePerMillion: 0.8 }))).toBe('$0.200 / $0.800');
    expect(priceText(model('a', { inputPricePerMillion: 0, outputPricePerMillion: 0 }))).toBe('free / free');
    expect(priceText(model('a'))).toBe('');
  });
});

describe('the models New thread pins', () => {
  const provider = (name: string, ...ids: string[]): OfferedProvider => ({
    name,
    displayName: name,
    budgetPrecision: 'strict',
    models: ids.map((id) => ({ id, contextLength: 100_000 })),
    defaultModel: ids[0] ?? null,
  });
  const offered = [provider('openrouter', 'deepseek', 'qwen'), provider('anthropic', 'sonnet')];
  const pinned = (byDefault: { provider: string | null; model: string | null }, recent: { provider: string; model: string }[]) =>
    pinnedChoices(offered, byDefault, recent).map((c) => `${c.provider.name}/${c.id}`);

  it('are the default, then the recent models, each once', () => {
    expect(
      pinned({ provider: 'openrouter', model: 'deepseek' }, [
        { provider: 'anthropic', model: 'sonnet' },
        { provider: 'openrouter', model: 'deepseek' },
        { provider: 'openrouter', model: 'qwen' },
      ]),
    ).toEqual(['openrouter/deepseek', 'anthropic/sonnet', 'openrouter/qwen']);
  });

  it('leave out what is no longer offered, and are empty with nothing to pin', () => {
    expect(pinned({ provider: 'openai', model: 'gpt-5' }, [{ provider: 'anthropic', model: 'opus' }])).toEqual([]);
    expect(pinned({ provider: null, model: null }, [])).toEqual([]);
  });
});
