const docsSearch = document.querySelector('#docsSearch');
const docsCards = [...document.querySelectorAll('.doc-card')];
const favoriteStorageKey = 'sharpstart-doc-favorites';
let favoriteIds = new Set();
try {
  favoriteIds = new Set(JSON.parse(localStorage.getItem(favoriteStorageKey) || '[]'));
} catch {
  favoriteIds = new Set();
}
let favoritesOnly = false;

const favoriteFilter = document.createElement('button');
favoriteFilter.className = 'docs-favorite-filter';
favoriteFilter.type = 'button';
favoriteFilter.textContent = 'Избранное';
favoriteFilter.setAttribute('aria-pressed', 'false');
docsSearch.closest('.docs-search').after(favoriteFilter);

function applyDocsFilters() {
  const query = docsSearch.value.trim().toLocaleLowerCase('ru');
  for (const card of docsCards) {
    const isFavorite = favoriteIds.has(card.querySelector('summary span').textContent.trim());
    card.hidden = !card.textContent.toLocaleLowerCase('ru').includes(query) || (favoritesOnly && !isFavorite);
    if (query && !card.hidden) card.open = true;
    const button = card.querySelector('.doc-favorite');
    button.textContent = isFavorite ? '★' : '☆';
    button.setAttribute('aria-pressed', String(isFavorite));
    button.setAttribute('aria-label', isFavorite ? 'Убрать из избранного' : 'Добавить в избранное');
  }
}

for (const card of docsCards) {
  const id = card.querySelector('summary span').textContent.trim();
  const button = document.createElement('button');
  button.className = 'doc-favorite';
  button.type = 'button';
  button.addEventListener('click', () => {
    if (favoriteIds.has(id)) favoriteIds.delete(id);
    else favoriteIds.add(id);
    localStorage.setItem(favoriteStorageKey, JSON.stringify([...favoriteIds]));
    applyDocsFilters();
  });
  card.insertBefore(button, card.firstChild);
}

docsSearch.addEventListener('input', applyDocsFilters);
favoriteFilter.addEventListener('click', () => {
  favoritesOnly = !favoritesOnly;
  favoriteFilter.setAttribute('aria-pressed', String(favoritesOnly));
  favoriteFilter.textContent = favoritesOnly ? 'Показать все темы' : 'Избранное';
  applyDocsFilters();
});
applyDocsFilters();
