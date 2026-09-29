(async () => {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 20000);
  let stage = 'origin';
  const read = async path => {
    const response = await fetch(path, {credentials: 'same-origin', cache: 'no-store', signal: controller.signal});
    if (response.headers.get('cf-mitigated') === 'challenge') throw {kind: 'challenge', status: 403};
    if (!response.ok) {
      const retry = response.headers.get('retry-after');
      const seconds = retry && /^\d+$/.test(retry) ? Number(retry) : Math.ceil((Date.parse(retry) - Date.now()) / 1000);
      throw {kind: 'http', status: response.status, retryAfter: Number.isFinite(seconds) ? Math.max(0, seconds) : null};
    }
    try {return await response.json();} catch {throw {kind: 'invalid_json', status: response.status};}
  };
  try {
    if (location.origin !== 'https://claude.ai') throw {kind: 'wrong_origin', status: 0};
    if (/^\/(login|signup)(\/|$)/.test(location.pathname)) throw {kind: 'login', status: 401};
    // Observe a challenge only to pause. Never interact with its controls.
    if (document.querySelector('#challenge-running, #challenge-form, iframe[src*="challenges.cloudflare.com"]') ||
        /just a moment|checking your browser|잠시만 기다리/i.test(document.title)) throw {kind: 'challenge', status: 403};
    const active = document.cookie.split(';').map(x => x.trim()).find(x => x.startsWith('lastActiveOrg='));
    let activeId = null;
    try {activeId = active ? decodeURIComponent(active.slice('lastActiveOrg='.length)) : null;} catch {}
    const validId = value => typeof value === 'string' && /^[a-zA-Z0-9-]+$/.test(value);
    let organizationId = validId(activeId) ? activeId : null;
    if (!organizationId) {
      stage = 'organizations';
      const response = await read('/api/organizations');
      const organizations = Array.isArray(response) ? response : response?.organizations;
      if (!Array.isArray(organizations)) throw {kind: 'unexpected_shape', status: 200};
      if (!organizations.length) throw {kind: 'no_organizations', status: 200};
      if (organizations.length !== 1) throw {kind: 'choose_organization', status: 409};
      organizationId = organizations[0]?.uuid;
      if (!validId(organizationId)) throw {kind: 'invalid_organization', status: 422};
    }
    stage = 'usage';
    return {ok: true, usage: await read('/api/organizations/' + encodeURIComponent(organizationId) + '/usage')};
  } catch (error) {
    return {ok: false, stage, kind: error.kind || 'network', status: error.status || 0, retryAfter: error.retryAfter || null};
  } finally {clearTimeout(timeout);}
})();
