const list = document.querySelector('#lessonList');
let TOTAL_LESSONS = 0;
let courseLessons = [];
const search = document.querySelector('#searchInput');
const filter = document.querySelector('#levelFilter');
const backdrop = document.querySelector('#modalBackdrop');
let lessons = [];
let currentLesson = null;
let completed = new Set(JSON.parse(localStorage.getItem('sharpstart-completed') || '[]'));
let passedExercises = new Set(JSON.parse(localStorage.getItem('sharpstart-exercises-passed') || '[]'));
let readingProgress = {};
let readingSaveTimer;

async function loadCourseIndex() {
  const response = await fetch('/api/lessons');
  if (!response.ok) throw new Error('Не удалось загрузить курс');
  courseLessons = await response.json();
  TOTAL_LESSONS = courseLessons.length;
  updateCourseMeta();
  renderProgress();
}

function updateCourseMeta() {
  document.querySelector('#heroLessonCount').textContent = TOTAL_LESSONS;
  document.querySelector('#lessonCount').textContent = TOTAL_LESSONS;
  document.querySelector('#progressCount').textContent = `${completed.size} / ${TOTAL_LESSONS}`;
  document.querySelector('#profileDoneCount').textContent = `${completed.size} / ${TOTAL_LESSONS}`;
  const minutes = courseLessons.reduce((sum, lesson) => sum + (Number(lesson.duration.match(/\d+/)?.[0]) || 0), 0);
  document.querySelector('#courseHours').textContent = `~${Math.max(1, Math.round(minutes / 60))} ч`;
  const labels = { 'Основы': 'база', 'Практика': 'упражнения', 'ООП': 'объекты', 'Дальше': 'следующие темы' };
  document.querySelectorAll('.track-item').forEach(item => {
    const title = item.querySelector('b')?.textContent.trim() || '';
    const level = title.includes('Основы') ? 'Основы' : title;
    const count = courseLessons.filter(lesson => lesson.level === level).length;
    const suffix = labels[level] || '';
    const small = item.querySelector('small');
    if (small) small.textContent = `${count} ${count === 1 ? 'урок' : count < 5 ? 'урока' : 'уроков'} · ${suffix}`;
  });
}

async function loadLessons() {
  try {
    if (!courseLessons.length) await loadCourseIndex();
    const params = new URLSearchParams();
    if (search.value.trim()) params.set('q', search.value.trim());
    if (filter.value) params.set('level', filter.value);
    const response = await fetch(`/api/lessons?${params}`);
    if (!response.ok) throw new Error('Не удалось загрузить уроки');
    lessons = await response.json();
    renderLessons();
  } catch {
    list.innerHTML = '<div class="loading">Не удалось загрузить уроки. Обнови страницу.</div>';
  }
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
}

function renderLessons() {
  document.querySelector('#lessonCount').textContent = lessons.length;
  document.querySelector('#emptyState').hidden = lessons.length !== 0;
  list.innerHTML = lessons.map(lesson => {
    const trackClass = lesson.level === 'Практика' ? 'practice' : lesson.level === 'Дальше' ? 'next' : lesson.level === 'ООП' ? 'oop' : '';
    const done = completed.has(lesson.id);
    return `<a class="lesson-row" data-id="${lesson.id}" href="/lesson/${encodeURIComponent(lesson.slug)}" aria-label="Открыть урок: ${escapeHtml(lesson.track)}">
      <span class="lesson-num ${done ? 'done' : ''}">${done ? '✓' : String(lesson.sortOrder).padStart(2, '0')}</span>
      <span class="lesson-info"><b>${escapeHtml(lesson.track)}</b><small>${escapeHtml(lesson.title)} · ${escapeHtml(lesson.description)}</small></span>
      <span class="lesson-meta"><span class="tag ${trackClass}">${escapeHtml(lesson.level)}</span><span class="duration">◷ ${escapeHtml(lesson.duration)}</span></span><span class="row-arrow" aria-hidden="true">›</span></a>`;
  }).join('');
}

function renderProgress() {
  const count = completed.size;
  const total = Math.max(TOTAL_LESSONS, 1);
  document.querySelector('#progressCount').textContent = `${count} / ${TOTAL_LESSONS}`;
  document.querySelector('#progressBar').style.width = `${Math.min(100, count / total * 100)}%`;
  document.querySelector('#progressPercent').textContent = count === 0 ? 'Отличное начало!' : count === TOTAL_LESSONS ? 'Курс завершён!' : `Уже ${Math.round(count / total * 100)}% пути`;
}

function closeModal() {
  if (!backdrop) return;
  backdrop.hidden = true;
  document.body.style.overflow = '';
}
document.querySelector('#modalClose').addEventListener('click', closeModal);
backdrop.addEventListener('click', event => { if (event.target === backdrop) closeModal(); });
document.addEventListener('keydown', event => { if (event.key === 'Escape' && !backdrop.hidden) closeModal(); });

document.querySelector('#continueBtn').addEventListener('click', () => {
  const next = courseLessons.find(lesson => !completed.has(lesson.id)) || courseLessons[0];
  if (next) window.location.href = `/lesson/${encodeURIComponent(next.slug)}`;
});
let timer;
search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(loadLessons, 180); });
filter.addEventListener('change', () => {
  document.querySelectorAll('.track-item').forEach(item => {
    const title = item.querySelector('b')?.textContent.trim() || '';
    const level = title.includes('Основы') ? 'Основы' : title;
    const active = level === filter.value;
    item.classList.toggle('selected', active);
    item.setAttribute('aria-pressed', String(active));
  });
  loadLessons();
});
document.querySelectorAll('.track-item').forEach(item => {
  const title = item.querySelector('b')?.textContent.trim() || '';
  const level = title.includes('Основы') ? 'Основы' : title;
  item.setAttribute('role', 'button');
  item.tabIndex = 0;
  item.dataset.track = level;
  const activate = () => {
    filter.value = filter.value === level ? '' : level;
    document.querySelectorAll('.track-item').forEach(other => {
      const active = other.dataset.track === filter.value;
      other.classList.toggle('selected', active);
      other.setAttribute('aria-pressed', String(active));
    });
    loadLessons();
  };
  item.addEventListener('click', activate);
  item.addEventListener('keydown', event => {
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); activate(); }
  });
});
renderProgress();
loadLessons();
const authBackdrop = document.querySelector('#authBackdrop');
const authForm = document.querySelector('#authForm');
const authError = document.querySelector('#authError');
const authEmail = document.querySelector('#authEmail');
const authPassword = document.querySelector('#authPassword');
const authName = document.querySelector('#authName');
const nameField = document.querySelector('#nameField');
const authSubmit = document.querySelector('#authSubmit');
document.querySelector('.demo-login')?.remove();
let authMode = 'login';
let sessionToken = localStorage.getItem('sharpstart-token');

function showAuth() {
  authBackdrop.hidden = false;
  document.body.style.overflow = 'hidden';
}
async function finishAuth(user, token) {
  sessionToken = token;
  localStorage.setItem('sharpstart-token', token);
  authBackdrop.hidden = true;
  document.body.style.overflow = '';
  document.querySelector('#profileButton').textContent = (user.name || user.email || 'У').slice(0, 1).toUpperCase();
  document.querySelector('#profileButton').title = user.email;
  document.querySelector('#profileName').textContent = user.name || 'Мой профиль';
  document.querySelector('#profileEmail').textContent = user.email || '';
  document.querySelector('#profileAvatar').textContent = (user.name || user.email || 'У').slice(0, 1).toUpperCase();
  document.querySelector('#profileFooterEmail').textContent = user.email || '';
  try { await loadAccountProgress(); }
  catch { console.warn('Не удалось загрузить серверный прогресс; оставлен локальный снимок.'); }
}
async function loadAccountProgress() {
  const response = await fetch('/api/progress', { headers: { Authorization: `Bearer ${sessionToken}` } });
  if (!response.ok) throw new Error('Не удалось загрузить прогресс аккаунта.');
  const progress = await response.json();
  completed = new Set(progress.completedLessons || []);
  passedExercises = new Set(progress.passedExercises || []);
  readingProgress = progress.readingProgress || {};
  renderGamification(progress.gamification);
  localStorage.setItem('sharpstart-completed', JSON.stringify(Array.from(completed)));
  localStorage.setItem('sharpstart-exercises-passed', JSON.stringify(Array.from(passedExercises)));
  Object.keys(localStorage).filter(key => key.startsWith('sharpstart-reading:')).forEach(key => localStorage.removeItem(key));
  Object.entries(readingProgress).forEach(([lessonId, percent]) => localStorage.setItem(`sharpstart-reading:${lessonId}`, String(percent)));
  renderProgress();
  if (lessons.length) renderLessons();
}
async function restoreSession() {
  if (!sessionToken) { showAuth(); return; }
  try {
    const response = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${sessionToken}` } });
    if (!response.ok) throw new Error('Сессия истекла');
    await finishAuth(await response.json(), sessionToken);
  } catch {
    localStorage.removeItem('sharpstart-token'); sessionToken = null; showAuth();
  }
}

document.querySelectorAll('[data-auth-mode]').forEach(button => button.addEventListener('click', () => {
  authMode = button.dataset.authMode;
  document.querySelectorAll('[data-auth-mode]').forEach(tab => tab.classList.toggle('active', tab === button));
  const registering = authMode === 'register';
  nameField.hidden = !registering;
  authName.required = registering;
  authPassword.autocomplete = registering ? 'new-password' : 'current-password';
  document.querySelector('#authTitle').textContent = registering ? 'Создай аккаунт' : 'С возвращением';
  document.querySelector('.auth-subtitle').textContent = registering ? 'Сохрани свой прогресс и начни практику.' : 'Войди, чтобы продолжить изучать C#.';
  authSubmit.innerHTML = registering ? 'Зарегистрироваться <span>→</span>' : 'Войти <span>→</span>';
  authError.textContent = '';
}));

authForm.addEventListener('submit', async event => {
  event.preventDefault();
  authError.textContent = '';
  authSubmit.disabled = true;
  authSubmit.textContent = authMode === 'login' ? 'Входим…' : 'Создаём аккаунт…';
  try {
    const response = await fetch(`/api/auth/${authMode === 'login' ? 'login' : 'register'}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: authEmail.value, password: authPassword.value, name: authName.value })
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.message || 'Не удалось войти. Проверь почту и пароль.');
    await finishAuth(result, result.token);
  } catch (error) {
    authError.textContent = error.message || 'Не удалось подключиться к серверу.';
  } finally {
    authSubmit.disabled = false;
    authSubmit.innerHTML = authMode === 'login' ? 'Войти <span>→</span>' : 'Зарегистрироваться <span>→</span>';
  }
});

const profileBackdrop = document.querySelector('#profileBackdrop');
const gamificationPanel = document.createElement('section');
gamificationPanel.className = 'gamification-panel';
gamificationPanel.innerHTML = '<div class="gamification-summary"><article><span>ОПЫТ</span><b id="profileXp">0 XP</b></article><article><span>СЕРИЯ</span><b id="profileStreak">0 дней</b></article></div><h3>Достижения</h3><div id="profileAchievements" class="achievement-grid"></div>';
document.querySelector('.profile-pane[data-profile-pane="dashboard"] .profile-progress-track').before(gamificationPanel);
function renderGamification(stats) {
  if (!stats) return;
  document.querySelector('#profileXp').textContent = `${stats.experiencePoints || 0} XP`;
  document.querySelector('#profileStreak').textContent = `${stats.currentStreak || 0} ${stats.currentStreak === 1 ? 'день' : 'дней'}`;
  const icons = { 'first-code': '⌁', 'first-lesson': '✓', 'steady-three': '🔥', 'first-hundred': '✦', 'quarter-course': '◈', fundamentals: '⌘' };
  document.querySelector('#profileAchievements').innerHTML = (stats.achievements || []).map(item => `<article class="achievement-card ${item.unlocked ? 'unlocked' : 'locked'}"><span>${icons[item.id] || '✧'}</span><div><b>${escapeHtml(item.title)}</b><small>${escapeHtml(item.description)}</small></div></article>`).join('');
}
function setProfileTab(tabName) {
  document.querySelectorAll('[data-profile-tab]').forEach(tab => {
    const active = tab.dataset.profileTab === tabName;
    tab.classList.toggle('active', active); tab.setAttribute('aria-selected', String(active));
  });
  document.querySelectorAll('[data-profile-pane]').forEach(pane => {
    const active = pane.dataset.profilePane === tabName;
    pane.hidden = !active; pane.classList.toggle('active', active);
  });
}
async function refreshProfile() {
  try { await loadAccountProgress(); } catch {}
  const count = completed.size;
  const percent = Math.round(count / TOTAL_LESSONS * 100);
  document.querySelector('#profileDoneCount').textContent = `${count} / ${TOTAL_LESSONS}`;
  document.querySelector('#profilePercent').textContent = `${percent}%`;
  document.querySelector('#profileProgressBar').style.width = `${percent}%`;
  try {
    const response = await fetch('/api/lessons');
    const items = await response.json();
    const next = items.find(item => !completed.has(item.id));
    document.querySelector('#profileNextLesson').textContent = next?.track || 'Курс пройден';
    document.querySelector('#profileLessonList').innerHTML = items.map(item => {
      const isDone = completed.has(item.id);
      return `<div class="profile-lesson ${isDone ? 'done' : ''}"><span>${isDone ? '✓' : String(item.id).padStart(2, '0')}</span><b>${item.track}</b><small>${isDone ? 'Пройдено' : item.level}</small></div>`;
    }).join('');
  } catch { document.querySelector('#profileLessonList').textContent = 'Не удалось загрузить прогресс.'; }
}
function openProfile() {
  if (!sessionToken) return showAuth();
  profileBackdrop.hidden = false; document.body.style.overflow = 'hidden';
  setProfileTab('dashboard'); refreshProfile();
}
function closeProfile() { profileBackdrop.hidden = true; document.body.style.overflow = ''; }
document.querySelector('#profileButton').addEventListener('click', openProfile);
document.querySelector('#profileClose').addEventListener('click', closeProfile);
profileBackdrop.addEventListener('click', event => { if (event.target === profileBackdrop) closeProfile(); });
document.querySelectorAll('[data-profile-tab]').forEach(tab => tab.addEventListener('click', () => setProfileTab(tab.dataset.profileTab)));
document.querySelector('#profileLogout').addEventListener('click', async () => {
  if (sessionToken) await fetch('/api/auth/logout', { method: 'POST', headers: { Authorization: `Bearer ${sessionToken}` } }).catch(() => {});
  localStorage.removeItem('sharpstart-token'); sessionToken = null; closeProfile(); showAuth();
});
document.querySelector('#markNotificationsRead').addEventListener('click', event => {
  document.querySelectorAll('.notification-item').forEach(item => item.classList.remove('unread'));
  event.currentTarget.textContent = 'Все уведомления прочитаны'; event.currentTarget.disabled = true;
});
document.querySelectorAll('#profileContinue, #profilePractice').forEach(link => link.addEventListener('click', () => closeProfile()));
document.addEventListener('keydown', event => { if (event.key === 'Escape' && !profileBackdrop.hidden) closeProfile(); });

const editor = document.querySelector('#codeEditor');
const editorBody = document.querySelector('#editorBody');
const monacoHost = document.querySelector('#monacoEditor');
const lineNumbers = document.querySelector('#lineNumbers');
const editorStatus = document.querySelector('#editorStatus');
const runButton = document.querySelector('#runCode');
const runOutput = document.querySelector('#runOutput');
const runOutputText = document.querySelector('#runOutputText');
const stdinInput = document.querySelector('#stdinInput');
let monacoEditor = null;
let compilerErrorLines = [];
let compilerMarkers = [];
let currentExerciseKey = 'free-practice';
let currentStarterCode = '';
let saveTimer;
const initialCode = 'using System;\n\nConsole.WriteLine("Привет, мир!");\n\n// Напиши свой код здесь';
editor.value = localStorage.getItem('sharpstart-code') ?? initialCode;
currentStarterCode = initialCode;

function getEditorCode() { return monacoEditor ? monacoEditor.getValue() : editor.value; }
function setEditorValue(value) {
  if (monacoEditor) monacoEditor.setValue(value);
  editor.value = value;
}
function focusCodeEditor() { monacoEditor ? monacoEditor.focus() : editor.focus(); }
function updateCompilerMarkers() {
  if (!monacoEditor || !window.monaco) return;
  monaco.editor.setModelMarkers(monacoEditor.getModel(), 'sharpstart', compilerMarkers);
}
function syncEditor() {
  const code = getEditorCode();
  if (!monacoEditor) {
    const lineCount = code.split('\n').length;
    lineNumbers.innerHTML = Array.from({ length: lineCount }, (_, index) => `<span class="${compilerErrorLines.includes(index + 1) ? 'error' : ''}">${index + 1}</span>`).join('');
    const before = code.slice(0, editor.selectionStart);
    const line = before.split('\n').length;
    const column = before.length - before.lastIndexOf('\n');
    document.querySelector('#cursorPosition').textContent = `Строка ${line}, столбец ${column}`;
  }
}
function persistDraft() {
  editorStatus.textContent = 'Сохраняем…';
  document.querySelector('#savedIndicator').textContent = '● Сохраняем…';
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    try {
      localStorage.setItem(`sharpstart-draft:${currentExerciseKey}`, getEditorCode());
      localStorage.setItem(`sharpstart-input:${currentExerciseKey}`, stdinInput.value);
      if (currentExerciseKey === 'free-practice') localStorage.setItem('sharpstart-code', getEditorCode());
      editorStatus.textContent = 'Черновик сохранён на этом устройстве';
      document.querySelector('#savedIndicator').textContent = '● Сохранено';
    } catch {
      editorStatus.textContent = 'Не удалось сохранить черновик';
      document.querySelector('#savedIndicator').textContent = '● Не сохранено';
    }
  }, 300);
}
function onEditorChanged() {
  compilerMarkers = []; compilerErrorLines = [];
  updateCompilerMarkers(); syncEditor(); persistDraft();
}
editor.addEventListener('input', onEditorChanged);
editor.addEventListener('click', syncEditor);
editor.addEventListener('keyup', syncEditor);
editor.addEventListener('scroll', () => { lineNumbers.scrollTop = editor.scrollTop; });
editor.addEventListener('keydown', event => {
  if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') { event.preventDefault(); runButton.click(); return; }
  if (event.key === 'Tab') {
    event.preventDefault();
    editor.setRangeText('    ', editor.selectionStart, editor.selectionEnd, 'end');
    editor.dispatchEvent(new Event('input', { bubbles: true }));
    return;
  }
  if (event.key === 'Enter' && editor.selectionStart === editor.selectionEnd) {
    const caret = editor.selectionStart;
    const lineStart = editor.value.lastIndexOf('\n', caret - 1) + 1;
    const lineEndIndex = editor.value.indexOf('\n', caret);
    const lineEnd = lineEndIndex < 0 ? editor.value.length : lineEndIndex;
    const currentLine = editor.value.slice(lineStart, lineEnd);
    const trim = currentLine.trim();
    const indent = currentLine.match(/^\s*/)?.[0] || '';
    const header = /^(if|else\s+if|for|foreach|while|switch|catch)\b.*\)\s*$/.test(trim);
    const declaration = /^(else|try|finally|do|class|struct|record|enum|interface|namespace)\b/.test(trim);
    const addSemicolon = caret === lineEnd && trim.length > 0 && !/[;{}:,]\s*$/.test(currentLine) && !trim.startsWith('//') && !header && !declaration;
    event.preventDefault();
    const extraIndent = trim.endsWith('{') ? '    ' : '';
    editor.setRangeText(`${addSemicolon ? ';' : ''}\n${indent}${extraIndent}`, caret, caret, 'end');
    editor.dispatchEvent(new Event('input', { bubbles: true }));
  }
});
stdinInput.value = localStorage.getItem(`sharpstart-input:${currentExerciseKey}`) || '';
stdinInput.addEventListener('input', persistDraft);
syncEditor();

document.querySelector('#copyCode').addEventListener('click', async event => {
  await navigator.clipboard.writeText(getEditorCode());
  event.currentTarget.textContent = 'Скопировано ✓';
  setTimeout(() => { event.currentTarget.textContent = 'Копировать'; }, 1400);
});
document.querySelector('#downloadCode').addEventListener('click', () => {
  const blob = new Blob([getEditorCode()], { type: 'text/plain;charset=utf-8' });
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob); link.download = 'Program.cs'; link.click();
  URL.revokeObjectURL(link.href);
});
function setEditorCode(code, exerciseKey = 'free-practice') {
  currentExerciseKey = exerciseKey;
  currentStarterCode = code || initialCode;
  setEditorValue(localStorage.getItem(`sharpstart-draft:${exerciseKey}`) ?? currentStarterCode);
  stdinInput.value = localStorage.getItem(`sharpstart-input:${exerciseKey}`) || '';
  compilerMarkers = []; compilerErrorLines = [];
  updateCompilerMarkers();
  runOutput.hidden = true;
  syncEditor(); persistDraft();
}
document.querySelector('#resetCode').addEventListener('click', () => {
  localStorage.removeItem(`sharpstart-draft:${currentExerciseKey}`);
  localStorage.removeItem(`sharpstart-input:${currentExerciseKey}`);
  setEditorValue(currentStarterCode);
  stdinInput.value = '';
  compilerMarkers = []; compilerErrorLines = []; updateCompilerMarkers();
  runOutput.hidden = true; syncEditor(); persistDraft();
});
document.querySelector('#openLessonPractice').addEventListener('click', () => {
  const lessonId = currentLesson?.id;
  setEditorCode(currentLesson?.starterCode || initialCode, lessonId ? `lesson-${lessonId}` : 'free-practice');
  closeModal();
  document.querySelector('#editor').scrollIntoView({ behavior: 'smooth' });
  setTimeout(focusCodeEditor, 450);
});

function parseCompilerDiagnostics(text) {
  const diagnostics = [];
  const pattern = /Program\.cs\((\d+),(\d+)(?:,\d+,\d+)?\):\s*error\s+(CS\d+):\s*([^\r\n]+)/g;
  for (const match of text.matchAll(pattern)) {
    const lineNumber = Number(match[1]);
    const column = Number(match[2]);
    diagnostics.push({
      startLineNumber: lineNumber, endLineNumber: lineNumber,
      startColumn: column, endColumn: column + 1,
      message: `${match[3]}: ${match[4].replace(/\s+\[[^\]]+\]$/, '')}`,
      severity: window.monaco?.MarkerSeverity.Error ?? 8
    });
  }
  return diagnostics;
}
runButton.addEventListener('click', async () => {
  const button = runButton;
  button.disabled = true; button.textContent = 'Запускаем…';
  runOutput.hidden = false; runOutput.classList.remove('error');
  document.querySelector('.run-output-title').textContent = 'Сборка и запуск программы';
  runOutputText.textContent = 'Компилируем C#…';
  compilerErrorLines = []; compilerMarkers = []; updateCompilerMarkers(); syncEditor();
  try {
    const response = await fetch('/api/code/run', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code: getEditorCode(), input: stdinInput.value })
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || 'Не удалось запустить код.');
    const elapsed = Number.isFinite(result.elapsedMilliseconds) ? `\n\nВремя: ${(result.elapsedMilliseconds / 1000).toFixed(2)} с` : '';
    runOutputText.textContent = (result.output || result.error || 'Готово.') + elapsed;
    if (!result.success) {
      runOutput.classList.add('error');
      document.querySelector('.run-output-title').textContent = result.timedOut ? 'Превышено время выполнения' : 'Проверь ошибки в коде';
      compilerMarkers = parseCompilerDiagnostics(result.stderr || result.output || '');
      compilerErrorLines = [...new Set(compilerMarkers.map(marker => marker.startLineNumber))];
      updateCompilerMarkers(); syncEditor();
    } else {
      document.querySelector('.run-output-title').textContent = 'Программа выполнена';
    }
  } catch (error) {
    runOutput.classList.add('error');
    document.querySelector('.run-output-title').textContent = 'Не удалось запустить';
    runOutputText.textContent = error.message;
  } finally {
    button.disabled = false; button.innerHTML = '▶ Запустить C# <kbd>Ctrl+Enter</kbd>';
  }
});

function shouldAutoFinishLine(line, caretAtEnd) {
  const trimmed = line.trim();
  const control = /^(if|else\s+if|for|foreach|while|switch|catch)\b.*\)\s*$/.test(trimmed);
  const declaration = /^(else|try|finally|do|class|struct|record|enum|interface|namespace)\b/.test(trimmed);
  return caretAtEnd && trimmed.length > 0 && !/[;{}:,]\s*$/.test(line) && !trimmed.startsWith('//') && !control && !declaration;
}
function loadMonacoEditor() {
  if (!window.require) {
    editorStatus.textContent = 'Базовый редактор активен; Monaco не загрузился';
    return;
  }
  const versionPath = 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs';
  window.MonacoEnvironment = {
    getWorkerUrl: () => `data:text/javascript;charset=utf-8,${encodeURIComponent(`self.MonacoEnvironment={baseUrl:'${versionPath}/'};importScripts('${versionPath}/base/worker/workerMain.js');`)}`
  };
  window.require.config({ paths: { vs: versionPath } });
  window.require(['vs/editor/editor.main'], () => {
    monacoEditor = window.monaco.editor.create(monacoHost, {
      value: editor.value,
      language: 'csharp', theme: 'vs-dark', automaticLayout: true,
      fontSize: 15, lineHeight: 24, tabSize: 4, insertSpaces: true,
      autoIndent: 'full', minimap: { enabled: false }, scrollBeyondLastLine: false,
      wordWrap: 'on', suggestOnTriggerCharacters: true, quickSuggestions: true,
      bracketPairColorization: { enabled: true },
      accessibilitySupport: 'auto'
    });
    editorBody.classList.add('monaco-active');
    monacoHost.hidden = false; editor.hidden = true; lineNumbers.hidden = true;
    monacoEditor.onDidChangeModelContent(onEditorChanged);
    monacoEditor.onDidChangeCursorPosition(event => {
      document.querySelector('#cursorPosition').textContent = `Строка ${event.position.lineNumber}, столбец ${event.position.column}`;
    });
    monacoEditor.onKeyDown(event => {
      if (event.keyCode !== window.monaco.KeyCode.Enter) return;
      const model = monacoEditor.getModel();
      const position = monacoEditor.getPosition();
      const line = model.getLineContent(position.lineNumber);
      const caretAtEnd = position.column - 1 === line.length;
      const trimmed = line.trim();
      const addSemicolon = shouldAutoFinishLine(line, caretAtEnd);
      const opensBlock = trimmed.endsWith('{');
      if (!addSemicolon && !opensBlock) return;
      const indent = line.match(/^\s*/)?.[0] || '';
      event.preventDefault();
      const insertedIndent = indent + (opensBlock ? '    ' : '');
      monacoEditor.executeEdits('sharpstart-auto-enter', [{
        range: new window.monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column),
        text: `${addSemicolon ? ';' : ''}\n${insertedIndent}`, forceMoveMarkers: true
      }]);
      monacoEditor.setPosition({ lineNumber: position.lineNumber + 1, column: insertedIndent.length + 1 });
    });
    monacoEditor.addCommand(window.monaco.KeyMod.CtrlCmd | window.monaco.KeyCode.Enter, () => runButton.click());
    window.monaco.languages.registerCompletionItemProvider('csharp', {
      provideCompletionItems(model, position) {
        const word = model.getWordUntilPosition(position);
        const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn };
        return { suggestions: [
          { label: 'cw', kind: window.monaco.languages.CompletionItemKind.Snippet, insertText: 'Console.WriteLine(${1});', insertTextRules: window.monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet, documentation: 'Вывести строку в консоль', range },
          { label: 'Console.ReadLine', kind: window.monaco.languages.CompletionItemKind.Snippet, insertText: 'Console.ReadLine()', documentation: 'Прочитать строку из поля ввода', range }
        ] };
      }
    });
    updateCompilerMarkers();
    editorStatus.textContent = 'Черновик сохраняется автоматически';
    monacoEditor.layout();
  }, () => {
    editorStatus.textContent = 'Базовый редактор активен; Monaco не загрузился';
  });
}
function highlightCSharp(code) {
  var source=String(code||''), rx=/\/\/[^\n]*|"(?:\\.|[^"\\])*"|\b(?:using|var|int|string|bool|decimal|double|char|void|static|class|record|struct|enum|interface|public|private|new|return|if|else|for|foreach|while|async|await|true|false|null|try|catch|finally|throw|override|virtual|event)\b|\b\d+\b/gm;
  var out='',last=0;
  for(const m of source.matchAll(rx)){out+=escapeHtml(source.slice(last,m.index));var t=m[0],kind=t.startsWith('//')?'comment':t.startsWith('"')?'string':/^\d/.test(t)?'number':'keyword';out+='<span class="syntax-'+kind+'">'+escapeHtml(t)+'</span>';last=m.index+t.length;}
  return out+escapeHtml(source.slice(last));
}
function renderLessonText(text){
  var tick=String.fromCharCode(96);
  return String(text||'').trim().split(/\n{2,}/).map(function(block){
    var chunks=block.split(tick.repeat(3));
    if(chunks.length===3)return '<pre class="lesson-code"><code>'+highlightCSharp(chunks[1])+'</code></pre>';
    if(block.indexOf('### ')===0)return '<h3>'+escapeHtml(block.slice(4))+'</h3>';
    if(block.indexOf('## ')===0)return '<h2>'+escapeHtml(block.slice(3))+'</h2>';
    return '<p>'+escapeHtml(block).replace(/\n/g,'<br>')+'</p>';
  }).join('');
}
function renderLessonPage(lesson){
  var page=document.createElement('section');page.id='lessonPage';page.className='lesson-page wrap';
  var pos=courseLessons.findIndex(function(x){return x.id===lesson.id;}),prev=pos>0?courseLessons[pos-1]:null,next=pos<courseLessons.length-1?courseLessons[pos+1]:null;
  var toc=courseLessons.map(function(x){return '<a class="'+(x.id===lesson.id?'current':'')+'" href="/lesson/'+encodeURIComponent(x.slug)+'"><span>'+String(x.sortOrder).padStart(2,'0')+'</span>'+escapeHtml(x.track)+'</a>';}).join('');
  var examples=(lesson.examples||[]).map(function(x,i){return '<figure class="lesson-example"><h3>'+escapeHtml(x.title||('Пример '+(i+1)))+'</h3>'+(x.explanation&&x.explanation!==lesson.content?renderLessonText(x.explanation):'')+'<pre class="lesson-code"><code>'+highlightCSharp(x.code)+'</code></pre>'+(x.output?'<p class="lesson-output">'+escapeHtml(x.output)+'</p>':'')+'</figure>';}).join('');
  var checks=(lesson.checks||[]).map(function(x){return '<article class="lesson-check" data-check-id="'+escapeHtml(x.id)+'"><h3>'+escapeHtml(x.question)+'</h3><div class="lesson-check-options">'+(x.options||[]).map(function(o){return '<button type="button" data-quiz-option="'+escapeHtml(o)+'">'+escapeHtml(o)+'</button>';}).join('')+'</div><p class="quiz-feedback" aria-live="polite"></p></article>';}).join('');
  var exercises=(lesson.exercises||[]).map(function(x,i){return '<article class="lesson-exercise"><h3>'+String(i+1).padStart(2,'0')+' · '+escapeHtml(x.title||'Практика')+'</h3><p>'+escapeHtml(x.prompt)+'</p><div class="exercise-hints">'+(x.hints||[]).map(function(h,n){return '<details><summary>Подсказка '+(n+1)+'</summary><p>'+escapeHtml(h)+'</p></details>';}).join('')+'</div><div class="exercise-actions"><button type="button" data-open-exercise="'+escapeHtml(x.id)+'">Открыть в редакторе</button>'+(x.hasTests?'<button type="button" data-check-exercise="'+escapeHtml(x.id)+'">Проверить решение</button>':'')+'<button type="button" data-show-solution="'+escapeHtml(x.id)+'">Показать решение</button></div><div class="exercise-result" aria-live="polite"></div><pre class="exercise-solution" hidden></pre></article>';}).join('');
  page.innerHTML='<div class="lesson-breadcrumb"><a href="/#course">Курс</a> / '+escapeHtml(lesson.level)+' / '+escapeHtml(lesson.track)+'</div><div class="reading-progress"><span>Прогресс чтения</span><div><i id="readingProgressBar"></i></div><b id="readingProgressText">0%</b></div><div class="lesson-mobile-tabs"><a href="#lesson-theory">Теория</a><a href="#lesson-exercises">Практика</a></div><div class="lesson-layout"><aside class="lesson-toc" aria-label="Оглавление курса">'+toc+'</aside><article class="lesson-article"><header class="lesson-header"><div class="eyebrow muted">'+escapeHtml(lesson.chapter)+' · '+escapeHtml(lesson.duration)+'</div><h1>'+escapeHtml(lesson.track)+'</h1><p>'+escapeHtml(lesson.description)+'</p></header><section class="lesson-theory" id="lesson-theory"><h2>После урока ты сможешь</h2><ul class="lesson-goals">'+(lesson.goals||[]).map(function(x){return '<li>'+escapeHtml(x)+'</li>';}).join('')+'</ul><h2>Теория</h2>'+renderLessonText(lesson.content)+(examples?'<h2>Примеры</h2>'+examples:'')+(checks?'<section class="lesson-checks"><h2>Проверь себя</h2>'+checks+'</section>':'')+(lesson.internals?'<details><summary>Как это устроено внутри</summary>'+renderLessonText(lesson.internals)+'</details>':'')+(lesson.sectionProject?'<aside class="section-project"><h3>Мини-проект раздела: '+escapeHtml(lesson.sectionProject.title)+'</h3><p>'+escapeHtml(lesson.sectionProject.description)+'</p></aside>':'')+'</section><section class="lesson-practice" id="lesson-exercises"><h2>Практика</h2><p>Открой задание в редакторе, внеси изменения и запусти код. Задания с автопроверкой проверяются на сервере.</p>'+exercises+'</section><nav class="lesson-navigation">'+(prev?'<a href="/lesson/'+encodeURIComponent(prev.slug)+'">← '+escapeHtml(prev.track)+'</a>':'<span></span>')+(next?'<a href="/lesson/'+encodeURIComponent(next.slug)+'">'+escapeHtml(next.track)+' →</a>':'<a href="/#course">К курсу</a>')+'</nav></article></div>';
  var lessonExtras='';
  if((lesson.commonMistakes||[]).length){lessonExtras+='<section class="lesson-mistakes"><h2>Частые ошибки</h2>'+(lesson.commonMistakes||[]).map(function(x){return '<details><summary>'+escapeHtml(x.mistake)+'</summary><p>'+escapeHtml(x.fix)+'</p></details>';}).join('')+'</section>';}
  if((lesson.takeaways||[]).length){lessonExtras+='<section class="lesson-takeaways"><h2>Главное из урока</h2><ul>'+(lesson.takeaways||[]).map(function(x){return '<li>'+escapeHtml(x)+'</li>';}).join('')+'</ul></section>';}
  if(lessonExtras)page.querySelector('.lesson-theory').insertAdjacentHTML('beforeend',lessonExtras);
  document.querySelectorAll('.hero,#course,#about').forEach(function(x){x.hidden=true;});document.querySelector('#editor').before(page);
  document.querySelector('#editor h2').textContent='Практика урока';document.title=lesson.track+' — SharpStart';document.querySelector('meta[name="description"]').content=lesson.description;
  document.querySelectorAll('.nav-link').forEach(function(x){if(x.textContent.trim()==='Курс')x.href='/#course';if(x.textContent.trim()==='Редактор')x.href='#editor';if(x.textContent.trim()==='О платформе')x.href='/#about';});
  document.querySelector('.brand').href='/';currentLesson=lesson;
  var first=lesson.exercises&&lesson.exercises[0];setEditorCode((first&&first.starterCode)||lesson.starterCode||initialCode,'lesson-'+lesson.id+'-'+((first&&first.id)||'practice'));
  page.addEventListener('click',handleLessonPageClick);
  var progressKey='sharpstart-reading:'+lesson.id;
  var initialRead=Number(readingProgress[lesson.id] ?? localStorage.getItem(progressKey) ?? 0);
  document.querySelector('#readingProgressBar').style.width=initialRead+'%';
  document.querySelector('#readingProgressText').textContent=initialRead+'%';
  function updateReadingProgress(){
    var article=document.querySelector('#lesson-theory');
    if(!article)return;
    var rect=article.getBoundingClientRect();
    var value=Math.round(Math.max(0,Math.min(1,(window.innerHeight-rect.top)/Math.max(article.scrollHeight,1)))*100);
    document.querySelector('#readingProgressBar').style.width=value+'%';
    document.querySelector('#readingProgressText').textContent=value+'%';
    if(value>initialRead){
      initialRead=value;readingProgress[lesson.id]=value;localStorage.setItem(progressKey,String(value));
      if(sessionToken){clearTimeout(readingSaveTimer);readingSaveTimer=setTimeout(function(){fetch('/api/progress/reading',{method:'PUT',headers:{'Content-Type':'application/json',Authorization:'Bearer '+sessionToken},body:JSON.stringify({lessonId:lesson.id,percent:value})}).then(function(response){if(response.ok)return loadAccountProgress();}).catch(function(){});},600);}
    }
  }
  window.addEventListener('scroll',updateReadingProgress,{passive:true});
  updateReadingProgress();
}
async function handleLessonPageClick(event){
  var option=event.target.closest('[data-quiz-option]');
  if(option){var card=option.closest('[data-check-id]'),feedback=card.querySelector('.quiz-feedback');var res=await fetch('/api/lessons/'+encodeURIComponent(currentLesson.slug)+'/quiz',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({checkId:card.dataset.checkId,option:option.dataset.quizOption})});var data=await res.json();option.classList.add(data.correct?'correct':'incorrect');feedback.textContent=data.message;return;}
  var open=event.target.closest('[data-open-exercise]');
  if(open){var ex=currentLesson.exercises.find(function(x){return x.id===open.dataset.openExercise;});if(ex){setEditorCode(ex.starterCode||currentLesson.starterCode||initialCode,'lesson-'+currentLesson.id+'-'+ex.id);document.querySelector('#editor').scrollIntoView({behavior:'smooth'});setTimeout(focusCodeEditor,450);}return;}
  var check=event.target.closest('[data-check-exercise]');if(check){await checkLessonExercise(check);return;}
  var show=event.target.closest('[data-show-solution]');
  if(show){var r=await fetch('/api/lessons/'+encodeURIComponent(currentLesson.slug)+'/solution?exerciseId='+encodeURIComponent(show.dataset.showSolution));var d=await r.json();var box=show.closest('.lesson-exercise').querySelector('.exercise-solution');box.innerHTML='<code>'+highlightCSharp(d.solution)+'</code>';box.hidden=false;show.hidden=true;}
}
async function checkLessonExercise(button){
  var feedback=button.closest('.lesson-exercise').querySelector('.exercise-result');button.disabled=true;button.textContent='Проверяем…';
  try{
    var headers={'Content-Type':'application/json'};if(sessionToken)headers.Authorization='Bearer '+sessionToken;
    var response=await fetch('/api/lessons/'+encodeURIComponent(currentLesson.slug)+'/check',{method:'POST',headers:headers,body:JSON.stringify({exerciseId:button.dataset.checkExercise,code:getEditorCode()})});
    var result=await response.json();if(!response.ok)throw new Error(result.error||'Не удалось проверить решение.');feedback.textContent=result.message;
    if(result.success){
      passedExercises.add(currentLesson.id+':'+button.dataset.checkExercise);localStorage.setItem('sharpstart-exercises-passed',JSON.stringify(Array.from(passedExercises)));
      var required=currentLesson.exercises.filter(function(x){return x.hasTests;}).map(function(x){return currentLesson.id+':'+x.id;});
      if(result.lessonCompleted||(required.length&&required.every(function(x){return passedExercises.has(x);}))) {completed.add(currentLesson.id);localStorage.setItem('sharpstart-completed',JSON.stringify(Array.from(completed)));renderProgress();feedback.textContent+=' Урок пройден.';}
      if(sessionToken)await loadAccountProgress().catch(function(){});
    }
  }
  catch(error){feedback.textContent=error.message||'Не удалось проверить решение.';feedback.classList.add('failed');}finally{button.disabled=false;button.textContent='Проверить решение';}
}
async function loadLessonPage(slug){
  await loadCourseIndex();var response=await fetch('/api/lessons/'+encodeURIComponent(slug));
  if(!response.ok){document.querySelectorAll('.hero,#course,#editor,#about').forEach(function(x){x.hidden=true;});var n=document.createElement('section');n.className='wrap lesson-not-found';n.innerHTML='<h1>Урок не найден</h1><a href="/#course">Вернуться к курсу</a>';document.querySelector('#top').prepend(n);return;}
  renderLessonPage(await response.json());
}
var lessonMatch=window.location.pathname.match(/^\/lesson\/([^/]+)\/?$/);
if(lessonMatch)loadLessonPage(decodeURIComponent(lessonMatch[1]));
loadMonacoEditor();
restoreSession();





