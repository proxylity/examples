const form = document.querySelector('#rule-lookup');
const results = document.querySelector('#rule-results');
const status = document.querySelector('#lookup-status');
let timer, controller, sequence = 0;

async function load() {
  clearTimeout(timer);
  controller?.abort();
  const current = ++sequence;
  results.disabled = true;
  if (!form.reportValidity()) {
    status.textContent = 'Enter a rule to load.';
    return;
  }
  controller = new AbortController();
  const params = new URLSearchParams(new FormData(form));
  const destination = `${form.getAttribute('action')}?${params}`;
  params.set('fragment', '1');
  status.textContent = 'Loading…';
  results.setAttribute('aria-busy', 'true');
  try {
    const response = await fetch(`${form.getAttribute('action')}?${params}`, { signal: controller.signal, credentials: 'same-origin' });
    if (current !== sequence) return;
    if (!response.ok) {
      if (response.status === 401) {
        const login = document.createElement('a');
        login.href = `/login?returnTo=${encodeURIComponent(destination)}`;
        login.textContent = 'Session expired. Log in to continue.';
        status.replaceChildren(login);
      } else {
        status.textContent = response.status === 400 ? 'Enter a valid domain, IP/CIDR or ASN.' : 'Unable to load the rule. Please retry.';
      }
      return;
    }
    const html = await response.text();
    if (current !== sequence) return;
    results.innerHTML = html;
    results.disabled = false;
    status.textContent = 'Loaded.';
    history.replaceState(null, '', destination);
  } catch (error) {
    if (current === sequence && error.name !== 'AbortError') {
      console.error('Rule lookup failed:', error);
      status.textContent = 'Unable to load the rule. Check your connection and retry.';
    }
  } finally {
    if (current === sequence) results.removeAttribute('aria-busy');
  }
}

form.addEventListener('submit', event => { event.preventDefault(); load(); });
function schedule() {
  clearTimeout(timer);
  controller?.abort();
  sequence++;
  results.disabled = true;
  results.removeAttribute('aria-busy');
  status.textContent = 'Pause typing to load the rule.';
  if (!form.elements.value.value.trim()) {
    status.textContent = 'Enter a rule to load.';
    return;
  }
  timer = setTimeout(load, 400);
}
form.elements.value.addEventListener('input', schedule);
