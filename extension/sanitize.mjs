export function sanitize(message) {
  const bucket = value => {
    const n = value?.utilization ?? value?.used_percentage;
    if (typeof n !== 'number' || !Number.isFinite(n) || n < 0 || n > 100) return null;
    const reset = value?.resets_at;
    return {utilization: n, resets_at: typeof reset === 'string' && Number.isFinite(Date.parse(reset)) ? new Date(reset).toISOString() : null};
  };
  if (message?.ok !== true) return {
    ok: false,
    kind: ['challenge', 'login', 'no_tab', 'paused', 'busy', 'http', 'invalid_json', 'unexpected_shape', 'no_organizations', 'choose_organization', 'invalid_organization', 'network', 'wrong_origin'].includes(message?.kind) ? message.kind : 'network',
    status: Number.isInteger(message?.status) ? Math.max(0, Math.min(599, message.status)) : 0,
    retryAfter: Number.isInteger(message?.retryAfter) ? Math.max(0, Math.min(86400, message.retryAfter)) : null
  };
  const raw = message.usage;
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {ok: false, kind: 'unexpected_shape', status: 200};
  let fable = null;
  for (const [key, value] of Object.entries(raw)) if (key.toLowerCase().includes('fable')) fable ??= bucket(value);
  for (const key of ['limits', 'model_scoped'])
    for (const item of Array.isArray(raw[key]) ? raw[key] : [])
      if (['name', 'display_name', 'model', 'model_name', 'id'].some(k => typeof item?.[k] === 'string' && item[k].toLowerCase().includes('fable'))) fable ??= bucket(item);
  const five = bucket(raw.five_hour);
  if (!five && !fable) return {ok: false, kind: 'unexpected_shape', status: 200};
  return {ok: true, usage: {five_hour: five, fable}};
}
