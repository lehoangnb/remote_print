const response = await fetch('/translations.json');
if (!response.ok) throw new Error('Unable to load translations');
const translations = await response.json();
let language = localStorage.getItem('remotePrintLanguage') === 'en' ? 'en' : 'vi';
const originalText = new WeakMap();
const originalAttributes = new WeakMap();
export const t = value => translations[language]?.[value] ?? value;
export const locale = () => language === 'en' ? 'en-US' : 'vi-VN';
export function translateTree(root) {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  while (walker.nextNode()) {
    const node = walker.currentNode;
    if (!originalText.has(node)) originalText.set(node, node.nodeValue);
    node.nodeValue = t(originalText.get(node));
  }
  const nodes = root instanceof Element ? [root, ...root.querySelectorAll('*')] : [...root.querySelectorAll('*')];
  for (const element of nodes) {
    let attrs = originalAttributes.get(element);
    if (!attrs) { attrs = {}; for (const name of ['aria-label', 'placeholder', 'data-label', 'title']) if (element.hasAttribute(name)) attrs[name] = element.getAttribute(name); originalAttributes.set(element, attrs); }
    for (const [name, value] of Object.entries(attrs)) element.setAttribute(name, t(value));
  }
}
function applyLanguage() {
  document.documentElement.lang = language;
  translateTree(document.documentElement);
  document.querySelectorAll('.language-select').forEach(select => { select.value = language; });
  window.dispatchEvent(new Event('languagechange-local'));
}
document.querySelectorAll('.language-select').forEach(select => select.addEventListener('change', () => {
  language = select.value === 'en' ? 'en' : 'vi';
  localStorage.setItem('remotePrintLanguage', language);
  applyLanguage();
}));
applyLanguage();
