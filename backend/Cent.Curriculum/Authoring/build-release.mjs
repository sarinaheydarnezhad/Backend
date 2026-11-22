// Offline extension of the existing Corpus format. Never imports, connects or activates.
import fs from 'node:fs'
import path from 'node:path'
import {fileURLToPath} from 'node:url'
import {createHash} from 'node:crypto'
import assert from 'node:assert/strict'
import {nodes, sourceUrl} from './Syllabus.mjs'
import {lessons as core} from './Core.mjs'
import {lessons as chemistry} from './Chemistry.mjs'
import {lessons as reading, passages} from './Reading.mjs'

const content = fileURLToPath(new URL('../../Cent.Module/Content/', import.meta.url))
const legacy = JSON.parse(fs.readFileSync(path.join(content, 'curriculum.json'), 'utf8'))
const sha = s => createHash('sha256').update(s).digest('hex')
const html = s => s.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;')
const fa = /[\u0600-\u06ff]/u
const lessons = [...reading, ...core, ...chemistry]
const releaseId = 'cent-content-2026-r1'
const syllabusRevisionId = 'cent-syllabus-2026-11-22-r1'
const date = '2026-11-22T00:00:00Z'
const subjects = ['reading', 'math', 'logic', 'biology', 'chemistry', 'physics']
const rights = 'Original Itatest educational content authored for this task; no CISIA or commercial questions copied.'
const reviewIdentity = 'Codex academic solution and editorial review, 2026-11-22; no independent specialist or psychometric certification'
const reviewDir = path.join(content, 'Review', releaseId)
fs.mkdirSync(reviewDir, {recursive: true})
const addition = {sources: [], chapters: [], topics: [], sections: [], exercises: [], questions: [], findings: []}
const release = {id: releaseId, syllabusRevisionId, sourceUrl, retrievedAt: date, nodes,
  topics: [], resources: [], reviews: [], passages: [], groups: []}
addition.release = release

// Old topic IDs stay intact. Mappings indicate structural affinity, never academic certification.
function legacyNodes(t) {
  const k = t.key; const title = t.title
  if (t.course === 'chemistry') return ['S17']
  if (t.course === 'logic') return /ch1[4-7]/.test(k) ? ['S10'] : /ch1[89]/.test(k) ? ['S09','S11'] : ['S09']
  if (t.course === 'biology') {
    if (/chapter1/.test(k)) return ['S12']
    if (/chapter2/.test(k)) return /فتوسنتز/.test(title) ? ['S13','S15'] : ['S13']
    return [/chapter3/.test(k) ? 'S14' : /chapter4/.test(k) ? 'S15' : 'S16']
  }
  if (t.course === 'physics') return [/chapter1/.test(k) ? 'S27' : /chapter2/.test(k) ? 'S28' : /chapter3/.test(k) ? 'S29' : /chapter[45]/.test(k) ? 'S30' : 'S31']
  if (t.course === 'math') {
    if (/mastercalss/.test(k)) return ['S02']
    if (/geometry/.test(k)) return ['S04']
    if (/chapter2/.test(k)) return [Number(k.match(/section-(\d+)/)?.[1]) >= 8 ? 'S04' : 'S03']
    if (/chapter6/.test(k)) return ['S07']
    if (/chapter7/.test(k)) return ['S08']
    if (/توابع/.test(k)) return /لگاریتم|نمایی/.test(title) ? ['S06'] : ['S05']
    if (/trig/.test(k)) return [] // Supplementary prerequisite, not a separate official nucleus.
    if (/algebraic/.test(k)) return ['S03']
  }
  return []
}
for (const t of legacy.topics) {
  const nodeKeys = legacyNodes(t)
  const supplementary = /CRISPR|PCR|تکامل|هورمون|ایمنی|بیان ژن|جمعیت|شرطی|پراکندگی/.test(t.title) || nodeKeys.length === 0
  release.topics.push({topicKey: t.key, nodeKeys, status: supplementary ? 'out-of-scope' : 'requires-source-verification',
    note: supplementary ? 'Retained supplementary material; not claimed as a distinct official requirement.' : 'Structural mapping only. Existing depth, rights, images and academic accuracy require specialist verification; preserved unchanged.'})
}

function addQuestion(q, lesson, context = {}) {
  const topicKey = `${lesson.subject}/updates-2026-r1.html#${lesson.id}`
  const key = `${topicKey}/question-${q.id}`
  assert.equal(q.options.length, 5, key)
  assert.equal(new Set(q.options.map(x => x.trim())).size, 5, key)
  assert.ok(q.correctAnswer >= 0 && q.correctAnswer < 5 && Number.isInteger(q.correctAnswer), key)
  assert.ok(!fa.test(q.text + q.options.join(' ') + q.explanation), key)
  // Stable per-item permutation removes the authored repeated A/B/C/D/E key pattern.
  const ordering = q.options.map((text, i) => ({text, i, hash: sha(key + ':' + i)})).sort((a,b) => a.hash.localeCompare(b.hash))
  const row = {key, course: lesson.subject, topicKey, sectionKey: topicKey, exerciseKey: key + '/exercise', text: q.text,
    options: ordering.map(x => x.text), correctAnswer: ordering.findIndex(x => x.i === q.correctAnswer),
    explanation: q.explanation, difficulty: q.difficulty, figureSvg: null, origin: 'authored-2026-reviewed'}
  addition.questions.push(row)
  addition.exercises.push({key: row.exerciseKey, sourceKey: `${lesson.subject}/updates-2026-r1.html`, sectionKey: topicKey,
    topicKey, course: lesson.subject, text: row.text, options: row.options, correctAnswer: row.correctAnswer,
    solution: row.explanation, difficulty: row.difficulty, figureSvg: null, status: 'reviewed'})
  release.reviews.push({key, revision: key + ':r1', status: 'academically-reviewed', reviewer: reviewIdentity,
    evidence: 'Answer independently checked against the worked rationale: ' + row.explanation, rights, ...context})
  release.resources.push({kind: 'question', key, subject: lesson.subject, topicKey, revision: key + ':r1', language: 'en', reviewStatus: 'agent-reviewed'})
  release.resources.push({kind: 'exercise', key: row.exerciseKey, subject: lesson.subject, topicKey, revision: key + ':r1', language: 'en', reviewStatus: 'agent-reviewed'})
  return key
}

for (const subject of subjects) {
  const sourceKey = `${subject}/updates-2026-r1.html`
  const selected = lessons.filter(l => l.subject === subject)
  const sections = selected.map((lesson, i) => {
    const key = sourceKey + '#' + lesson.id
    assert.ok(nodes.some(n => n.key === lesson.node && n.subject === subject))
    assert.ok(fa.test(lesson.teaching) && lesson.teaching.length > 300)
    addition.topics.push({key, chapterKey: sourceKey + '#chapter', course: subject, title: lesson.title, englishTitle: lesson.englishTitle, sortOrder: i + 1, foundation: true})
    release.topics.push({topicKey: key, nodeKeys: [lesson.node], status: 'valid', note: 'Original Persian foundation supplement with worked example and English assessment. Agent review; no independent academic certification.'})
    const text = `${lesson.title}\nهدف یادگیری: ${lesson.title}\n${lesson.teaching}\nنمونهٔ حل‌شده\n${lesson.example}\nخطای رایج و راهبرد آزمون\n${lesson.mistake}`
    const markup = `<section id="${lesson.id}"><h2>${html(lesson.title)}</h2><p>${html(lesson.teaching)}</p><h3>نمونهٔ حل‌شده</h3><p>${html(lesson.example)}</p><h3>خطای رایج و راهبرد آزمون</h3><p>${html(lesson.mistake)}</p></section>`
    release.resources.push({kind: 'lesson', key, subject, topicKey: key, revision: key + ':r1', language: 'fa', reviewStatus: 'agent-reviewed'})
    for (const q of lesson.questions) addQuestion(q, lesson)
    return {key, sourceKey, topicKey: key, heading: lesson.title, sortOrder: i + 1, text, html: markup}
  })
  addition.sections.push(...sections)
  const markup = `<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8"><title>${selected[0].title} — CEnT-S 2026</title></head><body><h1>CEnT-S — ${subject}</h1><p>ویرایش آموزشی ${syllabusRevisionId}</p>${sections.map(s => s.html).join('\n')}</body></html>\n`
  const file = path.join(content, 'Sources', sourceKey)
  fs.mkdirSync(path.dirname(file), {recursive: true}); fs.writeFileSync(file, markup)
  addition.sources.push({key: sourceKey, course: subject, file: sourceKey, title: `CEnT-S 2026 — ${selected[0].title} و درس‌های تکمیلی`, originalTitle: `Original ${subject} foundations ${releaseId}`, sha256: sha(markup), text: sections.map(s => s.text).join('\n\n')})
  addition.chapters.push({key: sourceKey + '#chapter', course: subject, title: `مبانی و تکمیل سرفصل CEnT-S — ${subject}`, sortOrder: 100})
}
for (const p of passages) {
  release.passages.push({id: p.id, subject: 'reading', genre: p.genre, level: 'B2-target (editorial estimate, uncalibrated)', text: p.text,
    rights, source: 'Original fictional educational text; not a CISIA passage', reviewStatus: 'agent-reviewed'})
  const lesson = reading.find(l => l.id === p.lessonId)
  const id = p.id + '-group'
  const keys = p.questions.map(q => addQuestion(q, lesson, {groupId: id, passageId: p.id}))
  release.groups.push({id, subject: 'reading', passageId: p.id, questionKeys: keys})
}

// Exhaustive structural/language audit, not a falsely claimed scientific review of 1,485 old items.
function audit(row, kind) {
  const explanation = kind === 'question' ? row.explanation : row.solution
  const flags = []
  if (fa.test(row.text + row.options.join(' ') + (explanation || ''))) flags.push('assessment-not-fully-English')
  if (row.options.length !== 5) flags.push('not-official-five-option-format')
  if (row.correctAnswer == null || row.correctAnswer < 0 || row.correctAnswer >= row.options.length) flags.push('missing-or-invalid-key')
  if (!explanation?.trim()) flags.push('missing-solution')
  if (row.figureSvg) flags.push('figure-requires-optical-review')
  if (legacy.findings.some(f => f.key === row.key && f.action === 'quarantined')) flags.push('previously-quarantined')
  flags.push('independent-academic-and-rights-review-pending')
  return {key: row.key, kind, subject: row.course, topicKey: row.topicKey, status: flags.includes('previously-quarantined') ? 'requires-correction' : 'requires-source-verification', flags}
}
const auditRows = [...legacy.questions.map(q => audit(q, 'question')), ...legacy.exercises.map(q => audit(q, 'exercise'))]
for (const q of legacy.questions) release.reviews.push({key: q.key, revision: q.key + ':legacy-preserved', status: 'requires-source-verification',
  reviewer: 'Automated structure/language audit only', evidence: 'See Review/' + releaseId + '/legacy-item-audit.json; not eligible for new mocks', rights: 'Source rights not independently verified'})
for (const row of auditRows.filter(r => r.flags.some(f => f !== 'independent-academic-and-rights-review-pending')))
  addition.findings.push({key: row.key, action: 'retained-for-review', reason: row.flags.join('; ')})
// Scientific notation is case sensitive: Aa x Aa is not AA x aa.
const stems = [...legacy.questions, ...addition.questions].map(q => q.text.trim().replace(/\s+/g, ' '))
assert.equal(new Set(stems).size, stems.length, 'Duplicate bank question stems: ' + stems.filter((s,i) => stems.indexOf(s) !== i).join(' | '))
assert.equal(new Set(release.topics.map(t => t.topicKey)).size, legacy.topics.length + addition.topics.length)
assert.equal(new Set(lessons.map(l => l.node)).size, nodes.length)
const json = value => JSON.stringify(value, null, 2) + '\n'
const outputs = {
  [path.join(content, 'curriculum-2026.json')]: json(addition),
  [path.join(reviewDir, 'legacy-item-audit.json')]: json(auditRows),
}
const before = {subjects: new Set(legacy.topics.map(t => t.course)).size, sources: legacy.sources.length, topics: legacy.topics.length, sourceSections: legacy.sections.length, exercises: legacy.exercises.length, questions: legacy.questions.length, passages: 0}
const after = {subjects: subjects.length, sources: legacy.sources.length + addition.sources.length, topics: legacy.topics.length + addition.topics.length, sourceSections: legacy.sections.length + addition.sections.length, newLessonSupplements: lessons.length, exercises: legacy.exercises.length + addition.exercises.length, questions: legacy.questions.length + addition.questions.length, passages: passages.length}
const counts = subjects.map(subject => ({subject, newLessons: addition.topics.filter(t => t.course === subject).length, candidateQuestions: addition.questions.filter(q => q.course === subject).length, required: {reading:10,math:15,logic:15,biology:10,chemistry:10,physics:5}[subject]}))
outputs[path.join(reviewDir, 'summary.json')] = json({releaseId, syllabusRevisionId, sourceUrl, retrievedAt: date, legacySha256: sha(fs.readFileSync(path.join(content,'curriculum.json'))), before, after, counts, auditedLegacyItems: auditRows.length, independentReview: 'pending; agent review is not external academic certification', activation: 'disabled; no DB connection or migration application'})
// Standalone printable teaching material. These are review artifacts, never public
// static website assets and never exposed by an unprotected download endpoint.
const materials = path.join(content, 'Materials', releaseId)
fs.mkdirSync(materials, {recursive: true})
for (const subject of subjects) {
  const relevant = lessons.filter(l => l.subject === subject)
  const renderQuestion = row => `<article lang="en" dir="ltr"><h4>${html(row.text)}</h4><ol type="A">${row.options.map(o => `<li>${html(o)}</li>`).join('')}</ol><details><summary>Answer and explanation</summary><p>${String.fromCharCode(65 + row.correctAnswer)}. ${html(row.explanation)}</p></details></article>`
  const teaching = relevant.map(l => {
    const key = `${subject}/updates-2026-r1.html#${l.id}`
    const section = addition.sections.find(s => s.key === key)
    const independent = addition.questions.filter(q => q.topicKey === key && !release.reviews.find(r => r.key === q.key)?.groupId)
    return section.html + independent.map(renderQuestion).join('\n')
  }).join('\n')
  const grouped = release.passages.filter(p => p.subject === subject).map(p => {
    const group = release.groups.find(g => g.passageId === p.id)
    return `<section lang="en" dir="ltr"><h2>Reading passage</h2>${p.text.split(/\n\s*\n/).map(t => `<p>${html(t)}</p>`).join('')}${group.questionKeys.map(k => renderQuestion(addition.questions.find(q => q.key === k))).join('')}</section>`
  }).join('\n')
  outputs[path.join(materials, subject + '.html')] = `<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8"><title>CEnT-S — ${subject} — ${syllabusRevisionId}</title><style>body{font-family:system-ui,sans-serif;max-width:55rem;margin:auto;padding:2rem;line-height:1.8}article,section{break-inside:avoid}details{margin-block:1rem}@media print{details{display:block}details>p{display:block}}</style></head><body><h1>${html(subject)} — مبانی و تمرین</h1><p>ویرایش ${syllabusRevisionId}. درس‌ها و مثال‌های آموزشی فارسی؛ سنجش انگلیسی. این متن آموزشی تألیفی است؛ درجهٔ دشواری برآورد آموزشی است.</p>${teaching}${grouped}<footer><p>مرجع دامنهٔ سرفصل: <a href="${sourceUrl}">CISIA syllabus</a> — retrieved ${date}. Original educational material; agent review only; independent specialist acceptance pending.</p></footer></body></html>\n`
}
for (const [file, value] of Object.entries(outputs)) fs.writeFileSync(file, value)
console.log(json({before, after, counts, auditedLegacyItems: auditRows.length}))
