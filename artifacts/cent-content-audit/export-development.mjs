// Standalone Phase 0 audit utility. Does not load or start application code.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { DatabaseSync } from 'node:sqlite';

const ROOT = 'D:/Lerona/Backend';
const DB = path.join(ROOT, 'backend/Cent.Preview/App_Data/preview.db');
const CONTENT = path.join(ROOT, 'backend/Cent.Module/Content');
const LABEL = 'DEVELOPMENT DATA';
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d{3}Z$/, 'Z');
const OUT = path.join(ROOT, 'artifacts/cent-content-audit', `cent-phase0-development-${stamp}`);
const sha = b => crypto.createHash('sha256').update(b).digest('hex');
const normalizedId = x => typeof x === 'string' ? x.toLowerCase() : x;
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const blank = x => x === null || x === undefined || (typeof x === 'string' && !x.trim());
const definitions = {
  cent_courses: ['Code', 'Title', 'EnglishTitle', 'SortOrder', 'TrainingId', 'Active'],
  cent_chapters: ['Id', 'ImportKey', 'CourseCode', 'SourceId', 'Title', 'SortOrder'],
  cent_topics: ['Id', 'ImportKey', 'ChapterId', 'CourseCode', 'Title', 'EnglishTitle', 'SortOrder', 'Foundation'],
  cent_trainings: ['Id', 'Title', 'Description', 'IncludedInSubscription', 'Published', 'CreatedAt'],
  cent_documents: ['Id', 'TrainingId', 'SourceId', 'Title', 'SortOrder', 'PageCount', 'Published', 'CreatedAt'],
  cent_document_pages: ['DocumentId', 'Number', 'Image', 'Width', 'Height'],
  cent_document_sections: ['Id', 'DocumentId', 'Title', 'StartPage'],
  cent_sources: ['Id', 'ImportKey', 'CourseCode', 'FileName', 'Title', 'OriginalTitle', 'Sha256', 'OriginalHtml', 'PlainText', 'ImportedAt'],
  cent_source_sections: ['Id', 'SourceId', 'TopicId', 'ImportKey', 'Heading', 'SortOrder', 'Text', 'Html'],
  cent_source_exercises: ['Id', 'SourceSectionId', 'TopicId', 'ImportKey', 'Text', 'OptionsJson', 'OriginalCorrectAnswer', 'Solution', 'Status', 'ReviewNote'],
  cent_questions: ['Id', 'Subject', 'TopicId', 'SourceSectionId', 'SourceExerciseId', 'ImportKey', 'Origin', 'FigureSvg', 'Topic', 'Difficulty', 'Text', 'OptionsJson', 'CorrectAnswer', 'Explanation', 'Kind', 'Active'],
  cent_curriculum_imports: ['PackageHash', 'ImportedAt', 'SourceCount', 'TopicCount', 'QuestionCount'],
};
const issueRows = [];
function issue(code, severity, table, id, field, detail) {
  issueRows.push({code, severity, table, id, field, detail});
}
function jsonSafe(v) {
  if (typeof v === 'bigint') return v <= BigInt(Number.MAX_SAFE_INTEGER) && v >= BigInt(Number.MIN_SAFE_INTEGER) ? Number(v) : v.toString();
  if (v && typeof v === 'object' && !ArrayBuffer.isView(v)) return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, jsonSafe(x)]));
  return v;
}
function stableId(key) {
  const b = crypto.createHash('sha256').update('cent-curriculum-v1:' + key).digest().subarray(0, 16);
  return [Buffer.from(b.subarray(0,4)).reverse().toString('hex'), Buffer.from(b.subarray(4,6)).reverse().toString('hex'), Buffer.from(b.subarray(6,8)).reverse().toString('hex'), b.subarray(8,10).toString('hex'), b.subarray(10,16).toString('hex')].join('-').toUpperCase();
}
function walk(dir) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir, {withFileTypes:true}).flatMap(e => e.isDirectory() ? walk(path.join(dir,e.name)) : [path.join(dir,e.name)]).sort();
}
function dbFingerprint() {
  return Object.fromEntries(['', '-wal', '-shm', '-journal'].map(s => {
    const p = DB + s;
    if (!fs.existsSync(p)) return [path.basename(p), {exists:false}];
    const st=fs.statSync(p);
    return [path.basename(p), {exists:true, bytes:st.size, modifiedUtc:st.mtime.toISOString(), sha256:sha(fs.readFileSync(p))}];
  }));
}
if (!fs.existsSync(DB)) throw Error('STOP: confirmed development SQLite database is missing. No fallback permitted.');
const before = dbFingerprint();
if (before['preview.db-wal'].exists && before['preview.db-wal'].bytes > 0) throw Error('STOP: nonempty SQLite WAL prevents an immutable main-file view. No alternate database will be used.');
const bundleBytes = fs.readFileSync(path.join(CONTENT, 'curriculum.json'));
const bundle = JSON.parse(bundleBytes.toString('utf8'));
const bundleHash = sha(bundleBytes);
const db = new DatabaseSync('file:'+DB.replaceAll('\\','/')+'?mode=ro&immutable=1', {readOnly:true, allowExtension:false});
const schema = {}, data = {};
let tables;
try {
  db.exec('PRAGMA query_only=ON; BEGIN');
  tables = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").all().map(x => x.name);
  for (const [table, columns] of Object.entries(definitions)) {
    if (!tables.includes(table)) throw Error(`STOP: expected CEnT content table ${table} is missing. No fallback permitted.`);
    const tableInfo = db.prepare(`PRAGMA table_info("${table}")`).all();
    for (const c of columns) if (!tableInfo.some(x => x.name === c)) throw Error(`STOP: expected column ${table}.${c} missing.`);
    const pk = tableInfo.filter(x=>x.pk).sort((a,b)=>a.pk-b.pk).map(x=>x.name);
    const stmt = db.prepare(`SELECT ${columns.map(x=>`"${x}"`).join(',')} FROM "${table}" ORDER BY ${pk.map(x=>`"${x}"`).join(',')}`);
    stmt.setReadBigInts(true);
    data[table] = stmt.all().map(jsonSafe);
    const count = Number(db.prepare(`SELECT COUNT(*) AS n FROM "${table}"`).get().n);
    if (data[table].length !== count) throw Error(`STOP: count mismatch for ${table}`);
    schema[table] = {columns:tableInfo, exportedColumns:columns, primaryKey:pk,
      foreignKeys:db.prepare(`PRAGMA foreign_key_list("${table}")`).all(),
      indexes:db.prepare(`PRAGMA index_list("${table}")`).all().map(i=>({...i, columns:db.prepare(`PRAGMA index_info("${i.name}")`).all()})), count};
    for (const c of tableInfo.filter(x=>!columns.includes(x.name))) issue('UNEXPORTED_NEW_COLUMN','warning',table,null,c.name,'Column is outside the explicit educational export allowlist.');
  }
  if (!data.cent_courses.length || !data.cent_sources.length || !data.cent_questions.length) throw Error('STOP: expected CEnT curriculum is absent. No alternate database will be used.');
  db.exec('ROLLBACK');
} finally { db.close(); }
const after = dbFingerprint();
const databaseUnchanged = same(before, after);
if (!databaseUnchanged) throw Error('STOP: source database or sidecar fingerprint changed during immutable extraction. No final package produced.');
fs.mkdirSync(OUT, {recursive:false});
const fileMeta = new Map();
function writeFile(rel, bytes, description, recordCount=null, source=null) {
  const dest=path.join(OUT,rel);
  fs.mkdirSync(path.dirname(dest),{recursive:true}); fs.writeFileSync(dest,bytes);
  fileMeta.set(rel.replaceAll('\\','/'), {description, recordCount, source});
}
function writeJson(rel, value, description, recordCount=null, source=null) {
  writeFile(rel, JSON.stringify(value,null,2)+'\n', description, recordCount, source);
}
function records(rel, values, description, source) {
  writeJson(rel,{datasetLabel:LABEL, source, recordCount:values.length, records:values},description,values.length,source);
}
const idx = Object.fromEntries(Object.entries(data).map(([t, rows])=>[t,new Map(rows.map(r=>[normalizedId(r.Id ?? r.Code ?? r.PackageHash),r]))]));
const get = (t,id) => idx[t].get(normalizedId(id));
const idFor = (t,r) => Object.fromEntries(schema[t].primaryKey.map(k=>[k,r[k]]));
const counts = Object.fromEntries(Object.entries(data).map(([k,v])=>[k,v.length]));
// Validate declared foreign keys in memory; excluded tables are never queried.
const brokenRelationships=[];
for(const [t,s] of Object.entries(schema)) for(const fk of s.foreignKeys) {
  if(!data[fk.table]) throw Error('Unexpected foreign key outside educational allowlist: '+t+'.'+fk.from);
  const targets = new Set(data[fk.table].map(x=>normalizedId(x[fk.to])));
  for(const row of data[t]) if(row[fk.from]!==null && !targets.has(normalizedId(row[fk.from]))) {
    const problem={table:t,primaryKey:idFor(t,row),field:fk.from,value:row[fk.from],targetTable:fk.table,targetColumn:fk.to};
    brokenRelationships.push(problem); issue('BROKEN_FOREIGN_KEY','error',t,idFor(t,row),fk.from,problem);
  }
}
const options = new Map();
for(const t of ['cent_questions','cent_source_exercises']) for(const r of data[t]) {
  let parsed;
  try {parsed=JSON.parse(r.OptionsJson); if(!Array.isArray(parsed)||parsed.some(x=>typeof x!=='string')) throw Error('Expected an array of strings');}
  catch(e){parsed=null; issue('INVALID_OPTIONS_JSON','error',t,r.Id,'OptionsJson',e.message);}
  options.set(t+':'+r.Id,parsed);
  if(parsed) {
    for(let n=0;n<parsed.length;n++) if(blank(parsed[n])) issue('BLANK_OPTION','warning',t,r.Id,'OptionsJson',`Option index ${n} is blank.`);
    if(new Set(parsed.map(x=>x.trim())).size<parsed.length) issue('DUPLICATE_OPTIONS','warning',t,r.Id,'OptionsJson','Repeated option text.');
    const answer=t==='cent_questions'?r.CorrectAnswer:r.OriginalCorrectAnswer;
    if(t==='cent_questions' && parsed.length<2) issue('INSUFFICIENT_OPTIONS','error',t,r.Id,'OptionsJson',`Only ${parsed.length} options.`);
    if(answer!==null && (!Number.isInteger(answer)||answer<0||answer>=parsed.length)) issue('ANSWER_OUT_OF_RANGE','error',t,r.Id,t==='cent_questions'?'CorrectAnswer':'OriginalCorrectAnswer',{answer,optionCount:parsed.length});
    if(t==='cent_source_exercises' && answer===null) issue('SOURCE_EXERCISE_WITHOUT_ANSWER_INDEX','info',t,r.Id,'OriginalCorrectAnswer',{status:r.Status,optionCount:parsed.length,note:'May be an open-response item; original solution is retained.'});
  }
}
const requiredText = {cent_courses:['Title'],cent_chapters:['Title'],cent_topics:['Title'],cent_trainings:['Title','Description'],cent_documents:['Title'],cent_document_sections:['Title'],cent_sources:['Title','OriginalHtml','PlainText'],cent_source_sections:['Heading','Text','Html'],cent_source_exercises:['Text','Solution'],cent_questions:['Text','Explanation','Difficulty','Topic']};
for(const [t,fields] of Object.entries(requiredText)) for(const r of data[t]) for(const f of fields) if(blank(r[f])) issue('EMPTY_CONTENT_FIELD','warning',t,idFor(t,r),f,'Empty or missing educational field.');
for(const t of data.cent_topics) if(blank(t.EnglishTitle)) issue('MISSING_ENGLISH_TOPIC_TITLE','info','cent_topics',t.Id,'EnglishTitle','Optional English topic label is empty.');
for(const q of data.cent_questions) {
  if(!['easy','medium','hard'].includes(q.Difficulty)) issue('UNKNOWN_DIFFICULTY','warning','cent_questions',q.Id,'Difficulty',q.Difficulty);
  if(q.TopicId===null) issue('QUESTION_UNMAPPED_TOPIC','warning','cent_questions',q.Id,'TopicId','Question has no stable topic relationship.');
  const topic=get('cent_topics',q.TopicId), section=get('cent_source_sections',q.SourceSectionId), exercise=get('cent_source_exercises',q.SourceExerciseId);
  if(topic && topic.CourseCode!==q.Subject) issue('QUESTION_SUBJECT_MISMATCH','error','cent_questions',q.Id,'Subject',{subject:q.Subject,topicCourse:topic.CourseCode});
  if(section && get('cent_sources',section.SourceId)?.CourseCode!==q.Subject) issue('QUESTION_SECTION_SUBJECT_MISMATCH','error','cent_questions',q.Id,'SourceSectionId',section.Id);
  if(exercise && normalizedId(exercise.TopicId)!==normalizedId(q.TopicId)) issue('QUESTION_EXERCISE_TOPIC_MISMATCH','warning','cent_questions',q.Id,'TopicId',{questionTopic:q.TopicId,questionTopicTitle:topic?.Title??null,exerciseId:exercise.Id,exerciseTopic:exercise.TopicId,exerciseTopicTitle:get('cent_topics',exercise.TopicId)?.Title??null,note:'Both IDs exist and match the bundle; this may be intentional retagging and needs editorial review.'});
}
for(const t of data.cent_topics) if(get('cent_chapters',t.ChapterId)?.CourseCode!==t.CourseCode) issue('TOPIC_CHAPTER_SUBJECT_MISMATCH','error','cent_topics',t.Id,'ChapterId',t.ChapterId);
const exerciseStatusCounts=Object.fromEntries([...new Set(data.cent_source_exercises.map(x=>x.Status))].sort().map(s=>[s,data.cent_source_exercises.filter(x=>x.Status===s).length]));
for(const e of data.cent_source_exercises.filter(x=>x.Status==='quarantined'||x.Status==='needs-review')) issue('SOURCE_EXERCISE_REVIEW_STATUS','warning','cent_source_exercises',e.Id,'Status',{status:e.Status,reviewNote:e.ReviewNote,scoredQuestionIds:data.cent_questions.filter(q=>normalizedId(q.SourceExerciseId)===normalizedId(e.Id)).map(q=>q.Id)});
// Keep byte arrays out of JSON and preserve all bytes as named, checksummed files.
for(const p of data.cent_document_pages) {
  const bytes=Buffer.from(p.Image);
  const valid=bytes.length>=12 && bytes.subarray(0,4).toString()==='RIFF' && bytes.subarray(8,12).toString()==='WEBP';
  const asset=`assets/document-pages/${p.DocumentId}/${p.Number}.${valid?'webp':'bin'}`;
  writeFile(asset,bytes,'Exact educational document page BLOB; no watermark was added.',1,'cent_document_pages.Image');
  p.Image={encoding:'external-file',path:asset,bytes:bytes.length,sha256:sha(bytes),mediaType:valid?'image/webp':'application/octet-stream'};
  if(!valid) issue('INVALID_WEBP_SIGNATURE','warning','cent_document_pages',{DocumentId:p.DocumentId,Number:p.Number},'Image','BLOB preserved as .bin.');
  if(p.Width<=0 || p.Height<=0) issue('INVALID_PAGE_DIMENSIONS','warning','cent_document_pages',{DocumentId:p.DocumentId,Number:p.Number},null,{width:p.Width,height:p.Height});
}
const documentCoverage=[];
for(const doc of data.cent_documents) {
  const pages=data.cent_document_pages.filter(p=>normalizedId(p.DocumentId)===normalizedId(doc.Id));
  const secs=data.cent_source_sections.filter(s=>normalizedId(s.SourceId)===normalizedId(doc.SourceId));
  const actual=doc.SourceId===null?pages.length:secs.length;
  const missing=doc.SourceId===null?Array.from({length:Math.max(0,doc.PageCount)},(_,i)=>i+1).filter(n=>!pages.some(p=>p.Number===n)):[];
  documentCoverage.push({documentId:doc.Id,sourceId:doc.SourceId,published:doc.Published,declaredPageCount:doc.PageCount,storage:doc.SourceId===null?'webp-pages':'source-sections',storedPageImages:pages.length,sourceSectionCount:secs.length,missingPageNumbers:missing});
  if(actual!==doc.PageCount||missing.length) issue('DOCUMENT_PAGE_COUNT_MISMATCH','error','cent_documents',doc.Id,'PageCount',{declared:doc.PageCount,actual,missingPageNumbers:missing});
  for(const p of pages) if(p.Number<1||p.Number>doc.PageCount) issue('DOCUMENT_PAGE_OUT_OF_RANGE','error','cent_document_pages',{DocumentId:p.DocumentId,Number:p.Number},'Number',doc.PageCount);
}
for(const s of data.cent_document_sections) {const doc=get('cent_documents',s.DocumentId); if(doc && (s.StartPage<1||s.StartPage>doc.PageCount)) issue('SECTION_PAGE_OUT_OF_RANGE','error','cent_document_sections',s.Id,'StartPage',s.StartPage);}
const noTopicSections=data.cent_source_sections.filter(s=>s.TopicId===null).map(s=>s.Id);
const noTopicExercises=data.cent_source_exercises.filter(s=>s.TopicId===null).map(s=>s.Id);
const zeroQuestionTopics=data.cent_topics.filter(t=>!data.cent_questions.some(q=>q.Active&&normalizedId(q.TopicId)===normalizedId(t.Id))).map(t=>({Id:t.Id,CourseCode:t.CourseCode,Title:t.Title}));
for(const t of zeroQuestionTopics) issue('TOPIC_WITHOUT_ACTIVE_QUESTIONS','warning','cent_topics',t.Id,null,t.Title);
for(const d of data.cent_documents.filter(d=>d.SourceId===null)) issue('IMAGE_ONLY_LESSON','info','cent_documents',d.Id,'SourceId','No stored machine-readable lesson text or original PDF. Exact page images are exported; no OCR or text reconstruction was performed.');

// Compare all fields persisted by the importer, not just counts and hash history.
const diffRows=[], comparisonSummary=[], formattingOnly=[];
function compare(kind, table, expectedRows, keyField='Id') {
  const actual=new Map(data[table].map(r=>[normalizedId(r[keyField]),r]));
  const expectedKeys=new Set(); let matched=0,differing=0,missing=0;
  for(const expected of expectedRows) {
    const key=normalizedId(expected[keyField]); expectedKeys.add(key);
    const row=actual.get(key);
    if(!row){missing++;diffRows.push({kind,table,id:expected[keyField],status:'missing-in-database',expected});continue;}
    const fields=[];
    for(const [field,value] of Object.entries(expected)) {
      let a=row[field], b=value;
      if(field==='OptionsJson') {
        try {if(a!==b && same(JSON.parse(a),JSON.parse(b))) formattingOnly.push({table,id:row[keyField],field}); a=JSON.parse(a);b=JSON.parse(b);} catch {}
      } else if(field==='Id'||field.endsWith('Id')) {a=normalizedId(a);b=normalizedId(b);}
      if(!same(a,b)) fields.push({field,actual:row[field],expected:value});
    }
    if(fields.length){differing++;diffRows.push({kind,table,id:row[keyField],status:'differing-fields',fields});} else matched++;
  }
  const extras=data[table].filter(r=>!expectedKeys.has(normalizedId(r[keyField]))).map(r=>r[keyField]);
  for(const id of extras) diffRows.push({kind,table,id,status:'database-only'});
  comparisonSummary.push({kind,table,bundled:expectedRows.length,database:data[table].length,exactSemanticMatches:matched,differing,missingInDatabase:missing,databaseOnly:extras.length});
}
const titleByCourse={math:['ریاضی','Mathematics'],logic:['منطق و تحلیل داده','Logic and data interpretation'],biology:['زیست‌شناسی','Biology'],chemistry:['شیمی','Chemistry'],physics:['فیزیک','Physics']};
compare('subjects','cent_courses',Object.entries(titleByCourse).map(([Code,[Title,EnglishTitle]],SortOrder)=>({Code,Title,EnglishTitle,SortOrder,TrainingId:stableId('training:'+Code),Active:1})),'Code');
compare('sources','cent_sources',bundle.sources.map(s=>({Id:stableId('source:'+s.key),ImportKey:s.key,CourseCode:s.course,FileName:s.file,Title:s.title,OriginalTitle:s.originalTitle,Sha256:s.sha256,PlainText:s.text})));
compare('chapters','cent_chapters',bundle.chapters.map(c=>({Id:stableId('chapter:'+c.key),ImportKey:c.key,CourseCode:c.course,SourceId:stableId('source:'+c.key.split('#')[0]),Title:c.title,SortOrder:c.sortOrder})));
compare('topics','cent_topics',bundle.topics.map(t=>({Id:stableId('topic:'+t.key),ImportKey:t.key,ChapterId:stableId('chapter:'+t.chapterKey),CourseCode:t.course,Title:t.title,EnglishTitle:t.englishTitle,SortOrder:t.sortOrder,Foundation:t.foundation?1:0})));
compare('source_sections','cent_source_sections',bundle.sections.map(s=>({Id:stableId('section:'+s.key),ImportKey:s.key,SourceId:stableId('source:'+s.sourceKey),TopicId:s.topicKey?stableId('topic:'+s.topicKey):null,Heading:s.heading,SortOrder:s.sortOrder,Text:s.text,Html:s.html})));
compare('source_exercises','cent_source_exercises',bundle.exercises.map(e=>{
  const finding=bundle.findings.find(f=>f.key===e.key);
  return {Id:stableId('exercise:'+e.key),ImportKey:e.key,SourceSectionId:stableId('section:'+e.sectionKey),TopicId:e.topicKey?stableId('topic:'+e.topicKey):null,Text:e.text,OptionsJson:JSON.stringify(e.options),OriginalCorrectAnswer:e.correctAnswer,Solution:e.solution,Status:finding?.action==='quarantined'?'quarantined':e.status,ReviewNote:finding?.reason??null};
}));
compare('questions','cent_questions',bundle.questions.map(q=>({Id:stableId('question:'+q.key),ImportKey:q.key,Subject:q.course,TopicId:stableId('topic:'+q.topicKey),SourceSectionId:stableId('section:'+q.sectionKey),SourceExerciseId:q.exerciseKey?stableId('exercise:'+q.exerciseKey):null,Topic:bundle.topics.find(t=>t.key===q.topicKey)?.title,Text:q.text,OptionsJson:JSON.stringify(q.options),CorrectAnswer:q.correctAnswer,Explanation:q.explanation,Difficulty:q.difficulty,FigureSvg:q.figureSvg,Origin:q.origin,Kind:'both',Active:1})));
compare('source_documents','cent_documents',bundle.sources.map(s=>({Id:stableId('document:'+s.key),SourceId:stableId('source:'+s.key),TrainingId:stableId('training:'+s.course),Title:s.title.slice(0,200),PageCount:bundle.sections.filter(x=>x.sourceKey===s.key).length,Published:1,SortOrder:Math.min(...bundle.chapters.filter(c=>c.key.split('#')[0]===s.key).map(c=>c.sortOrder))})));
compare('curriculum_trainings','cent_trainings',Object.entries(titleByCourse).map(([course,[title]])=>({Id:stableId('training:'+course),Title:title+' — منابع اصلی CEnT',Published:1,IncludedInSubscription:1,Description:'درس‌نامه‌ها و تمرین‌های منبع اصلی '+title+'. دسترسی خریداری‌شده در محیط مطالعه ارائه می‌شود.'})));
const sourceChecks=[];
for(const source of bundle.sources) {
  const full=path.resolve(CONTENT,'Sources',source.file);
  if(!full.startsWith(path.resolve(CONTENT,'Sources')+path.sep)) throw Error('Unsafe bundled source path');
  const row=get('cent_sources',stableId('source:'+source.key));
  const bytes=fs.existsSync(full)?fs.readFileSync(full):null;
  const check={key:source.key,databaseSourceId:row?.Id??null,file:source.file,exists:!!bytes,expectedSha256:source.sha256,actualSha256:bytes?sha(bytes):null,databaseDeclaredSha256:row?.Sha256??null,databaseHtmlUtf8Sha256:row?sha(Buffer.from(row.OriginalHtml,'utf8')):null,rawHtmlMatchesDatabase:!!bytes&&!!row&&bytes.toString('utf8')===row.OriginalHtml};
  check.checksumMatches=check.actualSha256===check.expectedSha256;
  sourceChecks.push(check);
  if(!bytes) issue('BUNDLED_SOURCE_MISSING','error','bundle',source.key,'file',source.file);
  else {
    writeFile('bundled/Sources/'+source.file,bytes,'Exact bundled HTML bytes; scripts and remote references are preserved but were not executed.',1,'repository bundled HTML');
    if(!check.checksumMatches) issue('BUNDLED_SOURCE_CHECKSUM_MISMATCH','error','bundle',source.key,'sha256',check);
  }
  if(row) writeFile(`assets/database-sources/${row.Id}.html`,Buffer.from(row.OriginalHtml,'utf8'),'Database OriginalHtml encoded as UTF-8, independently preserved from the bundled source.',1,'cent_sources.OriginalHtml');
  if(!check.rawHtmlMatchesDatabase) diffRows.push({kind:'raw_source_html',table:'cent_sources',id:row?.Id??stableId('source:'+source.key),status:'html-source-mismatch',details:check});
}
const actualHtmlFiles=walk(path.join(CONTENT,'Sources')).filter(p=>/\.html$/i.test(p));
const listedFiles=new Set(bundle.sources.map(s=>s.file.replaceAll('\\','/')));
const extraHtml=actualHtmlFiles.filter(p=>!listedFiles.has(path.relative(path.join(CONTENT,'Sources'),p).replaceAll('\\','/')));
for(const f of extraHtml){const rel=path.relative(path.join(CONTENT,'Sources'),f).replaceAll('\\','/');writeFile('bundled/Sources/'+rel,fs.readFileSync(f),'HTML present in repository but unlisted in curriculum JSON.',1,'repository');issue('UNLISTED_HTML_SOURCE','warning','bundle',rel,null,'Included as a repository-only source.');}
writeFile('bundled/curriculum.json',bundleBytes,'Exact UTF-8 bundled curriculum with source keys, original exercise difficulty/figures, authored questions and existing review findings.',null,'backend/Cent.Module/Content/curriculum.json');
for(const f of walk(path.join(CONTENT,'Review'))) {
  const rel=path.relative(path.join(CONTENT,'Review'),f).replaceAll('\\','/');
  writeFile('bundled/Review/'+rel,fs.readFileSync(f),'Existing derived curriculum review text/inventory; not an independent source or additional lesson.',null,'repository Content/Review');
}
for(const d of diffRows.filter(x=>x.status!=='database-only')) issue('DATABASE_BUNDLE_DIFFERENCE','warning',d.table,d.id,null,{kind:d.kind,status:d.status,fields:d.fields?.map(x=>x.field)});
const curriculumPresent=comparisonSummary.filter(x=>!['source_documents','curriculum_trainings'].includes(x.kind)).every(x=>x.missingInDatabase===0);
const curriculumMatches=curriculumPresent&&comparisonSummary.every(x=>x.missingInDatabase===0&&x.differing===0)&&sourceChecks.every(x=>x.checksumMatches&&x.rawHtmlMatchesDatabase);
const comparison={datasetLabel:LABEL,bundleSha256:bundleHash,bundleImportRecorded:data.cent_curriculum_imports.some(i=>i.PackageHash.toLowerCase()===bundleHash),identityRule:'Guid(SHA256(UTF8("cent-curriculum-v1:" + prefixed_key))[0..16]); first GUID fields use .NET little-endian layout',curriculumPresent,curriculumMatches,summary:comparisonSummary,sourceChecks,optionSerializationOnlyDifferences:formattingOnly,differences:diffRows,notes:['CreatedAt and ImportedAt are runtime metadata, not bundled fields, and are excluded from expected-value comparisons.','Database-only trainings/documents are preserved as additional development content.','Source exercise Difficulty and FigureSvg are present in the bundle but are not fields on CentSourceExercise; these values are preserved in bundled/curriculum.json.','OriginalHtml is compared separately against exact bundled HTML bytes decoded as UTF-8.']};

// Candidate duplicates: deterministic exact comparisons after whitespace normalization.
const duplicates=[];
const normText = s => String(s??'').normalize('NFC').replace(/\s+/gu,' ').trim();
function duplicateGroups(table,criterion,keyFn) {
  const groups=new Map();
  for(const r of data[table]) {const key=keyFn(r);if(!key)continue;if(!groups.has(key))groups.set(key,[]);groups.get(key).push(r);}
  for(const [key,rows] of groups) if(rows.length>1) duplicates.push({table,criterion,fingerprint:sha(key),recordIds:rows.map(x=>x.Id??idFor(table,x)),count:rows.length,classification:'candidate-only; no content removed'});
}
duplicateGroups('cent_questions','same subject + normalized statement + ordered options',r=>JSON.stringify([r.Subject,normText(r.Text),(options.get('cent_questions:'+r.Id)??[]).map(normText)]));
duplicateGroups('cent_source_exercises','same normalized statement + ordered options',r=>JSON.stringify([normText(r.Text),(options.get('cent_source_exercises:'+r.Id)??[]).map(normText)]));
duplicateGroups('cent_documents','same normalized title',r=>normText(r.Title));
duplicateGroups('cent_documents','identical ordered page-image hashes (image-only lessons)',r=>{
  if(r.SourceId!==null)return null;
  const pages=data.cent_document_pages.filter(p=>normalizedId(p.DocumentId)===normalizedId(r.Id)).sort((a,b)=>a.Number-b.Number);
  return pages.length?JSON.stringify(pages.map(p=>[p.Number,p.Image.sha256])):null;
});
duplicateGroups('cent_trainings','same normalized title',r=>normText(r.Title));
duplicateGroups('cent_sources','same full original HTML hash',r=>sha(r.OriginalHtml));
for(const g of duplicates) issue('DUPLICATE_CANDIDATE','info',g.table,g.recordIds,null,{criterion:g.criterion,count:g.count,fingerprint:g.fingerprint});

// Preserve references without downloading or opening remote resources.
const references=[];
function scanMarkup(markup,owner,baseFile=null) {
  if(!markup)return;
  const seen=new Set();
  const add=(attribute,value)=>{if(!value||seen.has(attribute+'\0'+value))return;seen.add(attribute+'\0'+value);references.push({owner,attribute,reference:value,baseFile});};
  const re=/\b(src|href|xlink:href|poster|data-src|srcset)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/gi;
  for(const m of markup.matchAll(re)) {
    if(m[1].toLowerCase()==='srcset') {for(const item of (m[2]??m[3]??m[4]).split(','))add('srcset',item.trim().split(/\s+/)[0]);}
    else add(m[1].toLowerCase(),m[2]??m[3]??m[4]);
  }
  for(const m of markup.matchAll(/url\(\s*['"]?([^'"\)]+)['"]?\s*\)/gi))add('css-url',m[1].trim());
  for(const m of markup.matchAll(/@import\s+['"]([^'"]+)['"]/gi))add('css-import',m[1]);
}
for(const s of bundle.sources) {const f=path.join(CONTENT,'Sources',s.file);if(fs.existsSync(f))scanMarkup(fs.readFileSync(f,'utf8'),{kind:'bundled-source',key:s.key},s.file);}
for(const s of data.cent_sources)scanMarkup(s.OriginalHtml,{table:'cent_sources',id:s.Id},s.FileName);
for(const s of data.cent_source_sections)scanMarkup(s.Html,{table:'cent_source_sections',id:s.Id},get('cent_sources',s.SourceId)?.FileName??null);
for(const q of data.cent_questions)scanMarkup(q.FigureSvg,{table:'cent_questions',id:q.Id,field:'FigureSvg'});
for(const e of bundle.exercises)scanMarkup(e.figureSvg,{kind:'bundled-exercise',key:e.key,field:'figureSvg'});
const copiedAssets=new Set();
for(const ref of references) {
  const value=ref.reference.replaceAll('&amp;','&');
  if(/^(https?:)?\/\//i.test(value)){ref.status='external-not-fetched';}
  else if(value.startsWith('#')){ref.status='in-document-fragment';}
  else if(/^data:/i.test(value)){ref.status='embedded-data-preserved';}
  else if(/^[a-z][a-z0-9+.-]*:/i.test(value)){ref.status='non-file-uri-preserved';}
  else if(ref.baseFile) {
    let clean=value.split(/[?#]/)[0];try{clean=decodeURIComponent(clean);}catch{}
    const target=path.resolve(CONTENT,'Sources',path.dirname(ref.baseFile),clean);
    if(!target.startsWith(path.resolve(CONTENT,'Sources')+path.sep)) {ref.status='outside-bundled-source-root';}
    else if(fs.existsSync(target)&&fs.statSync(target).isFile()) {
      const rel=path.relative(path.join(CONTENT,'Sources'),target).replaceAll('\\','/');
      ref.status='included-local-file';ref.exportPath='bundled/Sources/'+rel;
      if(!copiedAssets.has(rel)&&!fileMeta.has(ref.exportPath)) {writeFile(ref.exportPath,fs.readFileSync(target),'Locally referenced curriculum asset, exact bytes.',1,'bundled HTML reference');copiedAssets.add(rel);}
    } else ref.status='missing-local-file';
  } else ref.status='relative-reference-without-base';
}
for(const r of references.filter(r=>['missing-local-file','outside-bundled-source-root','relative-reference-without-base'].includes(r.status))) issue('UNRESOLVED_ASSET_REFERENCE','warning','assets',r.owner,null,{reference:r.reference,status:r.status});
const assetStatusCounts=Object.fromEntries([...new Set(references.map(r=>r.status))].sort().map(s=>[s,references.filter(r=>r.status===s).length]));

const subjectForDocument=d=>d.SourceId?get('cent_sources',d.SourceId)?.CourseCode??null:data.cent_courses.find(c=>normalizedId(c.TrainingId)===normalizedId(d.TrainingId))?.Code??null;
const subjectForExercise=e=>get('cent_sources',get('cent_source_sections',e.SourceSectionId)?.SourceId)?.CourseCode??null;
const subjectCounts=[...data.cent_courses.map(c=>c.Code),null].map(code=>{
  const docs=data.cent_documents.filter(d=>subjectForDocument(d)===code);
  const docIds=new Set(docs.map(d=>normalizedId(d.Id)));
  return {subjectCode:code,subjectTitle:code===null?'UNASSIGNED — no stored subject relationship':get('cent_courses',code)?.Title,
    chapters:data.cent_chapters.filter(x=>x.CourseCode===code).length,topics:data.cent_topics.filter(x=>x.CourseCode===code).length,
    sources:data.cent_sources.filter(x=>x.CourseCode===code).length,lessons:docs.length,sourceBasedLessons:docs.filter(x=>x.SourceId!==null).length,imageBasedLessons:docs.filter(x=>x.SourceId===null).length,
    documentPages:data.cent_document_pages.filter(p=>docIds.has(normalizedId(p.DocumentId))).length,
    documentSections:data.cent_document_sections.filter(s=>docIds.has(normalizedId(s.DocumentId))).length,
    sourceSections:data.cent_source_sections.filter(s=>(get('cent_sources',s.SourceId)?.CourseCode??null)===code).length,
    sourceExercises:data.cent_source_exercises.filter(e=>subjectForExercise(e)===code).length,
    questions:data.cent_questions.filter(q=>q.Subject===code).length,activeQuestions:data.cent_questions.filter(q=>q.Subject===code&&q.Active).length,
    trainings:code===null?data.cent_trainings.filter(t=>!data.cent_courses.some(c=>normalizedId(c.TrainingId)===normalizedId(t.Id))).length:data.cent_trainings.filter(t=>normalizedId(t.Id)===normalizedId(get('cent_courses',code)?.TrainingId)).length};
});
const topicCounts=data.cent_topics.map(t=>{
  const sections=data.cent_source_sections.filter(s=>normalizedId(s.TopicId)===normalizedId(t.Id));
  const sourceIds=new Set(sections.map(s=>normalizedId(s.SourceId)));
  const q=data.cent_questions.filter(q=>normalizedId(q.TopicId)===normalizedId(t.Id));
  return {topicId:t.Id,importKey:t.ImportKey,subjectCode:t.CourseCode,chapterId:t.ChapterId,title:t.Title,sourceSectionCount:sections.length,sourceIds:[...sourceIds].map(id=>get('cent_sources',id)?.Id),lessonIds:data.cent_documents.filter(d=>sourceIds.has(normalizedId(d.SourceId))).map(d=>d.Id),sourceExerciseCount:data.cent_source_exercises.filter(e=>normalizedId(e.TopicId)===normalizedId(t.Id)).length,questionCount:q.length,activeQuestionCount:q.filter(q=>q.Active).length,questionIds:q.map(q=>q.Id),difficultyCounts:Object.fromEntries(['easy','medium','hard'].map(d=>[d,q.filter(q=>q.Difficulty===d).length]))};
});
const hierarchy=data.cent_courses.map(c=>({subject:c,training:c.TrainingId?get('cent_trainings',c.TrainingId)??null:null,chapters:data.cent_chapters.filter(x=>x.CourseCode===c.Code).sort((a,b)=>a.SortOrder-b.SortOrder).map(ch=>({...ch,topics:data.cent_topics.filter(t=>normalizedId(t.ChapterId)===normalizedId(ch.Id)).sort((a,b)=>a.SortOrder-b.SortOrder).map(t=>({...t,sourceSectionIds:data.cent_source_sections.filter(s=>normalizedId(s.TopicId)===normalizedId(t.Id)).map(s=>s.Id),questionIds:data.cent_questions.filter(q=>normalizedId(q.TopicId)===normalizedId(t.Id)).map(q=>q.Id)}))})),sourceIds:data.cent_sources.filter(x=>x.CourseCode===c.Code).map(s=>s.Id),documentIds:data.cent_documents.filter(d=>subjectForDocument(d)===c.Code).map(d=>d.Id)}));
const answerChoices=[];
for(const t of ['cent_questions','cent_source_exercises'])for(const r of data[t])for(const [position,text] of (options.get(t+':'+r.Id)??[]).entries())answerChoices.push({parentTable:t,parentId:r.Id,position,text,correct:r[t==='cent_questions'?'CorrectAnswer':'OriginalCorrectAnswer']===null?null:r[t==='cent_questions'?'CorrectAnswer':'OriginalCorrectAnswer']===position});
const bankDistributions={byDifficulty:Object.fromEntries([...new Set(data.cent_questions.map(q=>q.Difficulty))].sort().map(d=>[d,data.cent_questions.filter(q=>q.Difficulty===d).length])),byOrigin:Object.fromEntries([...new Set(data.cent_questions.map(q=>q.Origin))].sort().map(d=>[d,data.cent_questions.filter(q=>q.Origin===d).length])),byKind:Object.fromEntries([...new Set(data.cent_questions.map(q=>q.Kind))].sort().map(d=>[d,data.cent_questions.filter(q=>q.Kind===d).length]))};
const runtimeExamRules={datasetLabel:LABEL,source:'backend/Cent.Module/CentPractice.cs:CentPractice.Start and Entities.cs:CentAttempt',storedReusableMockExamDefinitions:0,records:[],supportedKinds:['diagnostic','simulator','practice'],questionLimit:20,durationMinutes:60,eligibility:'Active questions with Kind equal to the requested kind or both; optional subject and stable TopicId filters.',selection:'Randomized round robin across subjects; interleaved topic queues within subjects. There is no fixed stored exam sequence or static exam template.',persistence:'Selections and answer keys are snapshotted into owner-linked cent_attempts / cent_attempt_questions. Those tables were not read or exported.',limitations:'Runtime behavior is documented from source inspection only. No attempt or exam was generated during this audit.'};
const excludedTables=tables.filter(t=>!Object.hasOwn(definitions,t));
const privacy={mode:'Explicit table AND column allowlist; no user-table SELECTs, API calls, login, seeding, application startup, migrations, or external network access.',excludedTables,excludedCategories:['all users and account identifiers','authentication sessions and OAuth state','personal notes and study progress','products, prices, purchases, orders, entitlements and payment events','learner attempts, answer snapshots, results and AI analyses','administrator audit events','all Lerona tables and records','whole SQLite database and backups'],exportedContentTimestamps:'Only educational content creation/import timestamps; no owner or actor metadata.'};
const filesByTable={cent_courses:'subjects',cent_chapters:'chapters',cent_topics:'topics',cent_trainings:'trainings',cent_documents:'lessons',cent_document_pages:'document_pages',cent_document_sections:'document_sections',cent_sources:'sources',cent_source_sections:'source_sections',cent_source_exercises:'source_exercises',cent_questions:'questions',cent_curriculum_imports:'curriculum_imports'};
for(const [table,name] of Object.entries(filesByTable))records('database/'+name+'.json',data[table],`All allowlisted educational fields from ${table}, including unpublished/inactive rows if present.`,table);
records('derived/answer_choices.json',answerChoices,'Parsed ordered choices; composite identity is parentTable + parentId + position (zero-based). Original OptionsJson remains in parent records.','derived from database OptionsJson');
records('derived/subject_hierarchy.json',hierarchy,'Subject/chapter/topic tree with stable relationship IDs; full texts remain in database exports.','derived from database relationships');
records('derived/counts_by_subject.json',subjectCounts,'Counts include an explicit unassigned bucket; no subject was inferred from lesson titles.','derived from database relationships');
records('derived/counts_by_topic.json',topicCounts,'Per-topic question/difficulty and exercise counts, source links and lesson IDs. Lesson associations can overlap across topics.','derived from database relationships');
records('derived/document_coverage.json',documentCoverage,'Page counts distinguish source-section virtual pages from image BLOB pages.','derived from database relationships');
writeJson('derived/unmapped_content.json',{datasetLabel:LABEL,sourceSectionIdsWithoutTopic:noTopicSections,sourceExerciseIdsWithoutTopic:noTopicExercises,questionIdsWithoutTopic:data.cent_questions.filter(q=>q.TopicId===null).map(q=>q.Id),lessonIdsWithoutStoredSubject:data.cent_documents.filter(d=>subjectForDocument(d)===null).map(d=>d.Id),topicsWithoutActiveQuestions:zeroQuestionTopics},'Exact IDs of content without topic/subject assessment links.');
records('derived/question_groupings.json',topicCounts.map(t=>({groupType:'derived-topic-grouping',topicId:t.topicId,subjectCode:t.subjectCode,questionIds:t.questionIds})),'Convenience groupings by existing TopicId; these are derived views, not stored question-group entities.','derived');
writeJson('derived/mock_exams.json',runtimeExamRules,'No reusable mock templates exist in this schema; source-inspected runtime assembly rules and exclusion of private attempts.',0,'source inspection');
writeJson('derived/study_notes.json',{datasetLabel:LABEL,recordCount:0,records:[],status:'No separately modelled editorial study-note entity was identified.',educationalTextLocations:['database/sources.json: PlainText, OriginalHtml','database/source_sections.json: Text, Html','bundled/curriculum.json and bundled/Sources'],excluded:'cent_notes is personal user-generated annotation data; it was neither queried nor exported.'},'Explains where editorial study text is stored and why personal notes are excluded.',0,'source inspection');
writeJson('audit/schema.json',{datasetLabel:LABEL,tables:schema},'Actual SQLite columns, primary keys, content-only foreign keys and indexes.');
writeJson('audit/comparison.json',comparison,'Record-by-record expected importer values, stable GUID checks, raw HTML checksums, exact missing/differing IDs and fields.');
records('audit/issues.json',issueRows,'Mechanical inventory findings with exact IDs; academic correctness has not been assessed.','audit');
records('audit/duplicate_candidates.json',duplicates,'Normalized exact duplicate candidates; repeated titles alone do not prove duplicate content.','audit');
records('audit/asset_references.json',references,'HTML/SVG/CSS references found without executing markup or fetching remote files. Local resolution and export paths included.','source markup');
writeJson('audit/privacy_scope.json',{datasetLabel:LABEL,...privacy},'Explicit exclusions and content-only export scope.');
const inspectedFiles={
  'CENT.md':'Module isolation and historical curriculum documentation; actual database/package take precedence over historical counts.',
  'backend/Cent.Module/CentDbContext.cs':'Cent DbSets, table mappings and SQL Server fallback factory; code read only, never instantiated.',
  'backend/Cent.Module/CurriculumEntities.cs':'Curriculum properties, keys and relationships.',
  'backend/Cent.Module/Entities.cs':'Lesson/question properties; distinction between content and user-owned entities.',
  'backend/Cent.Module/CurriculumPackage.cs':'Bundled corpus DTOs, course definitions and deterministic GUID algorithm.',
  'backend/Cent.Module/CentCurriculumImporter.cs':'Expected bundled-to-database mappings; inspected, never executed.',
  'backend/Cent.Module/CentPractice.cs':'Answer indexing and runtime assessment assembly; inspected, never executed.',
  'backend/Cent.Module/CentContent.cs':'Reader storage and personal-note separation; inspected, never executed.',
  'backend/Cent.Module/CentAdmin.cs':'Image storage and publication constraints; inspected, never executed.',
  'backend/Cent.Module/CentLearning.cs':'Source-section reader and curriculum hierarchy.',
  'backend/Cent.Module/CentModule.cs':'Connection configuration precedence; inspected, never loaded.',
  'backend/Cent.Module/Cent.Module.csproj':'JSON and HTML EmbeddedResource declarations.',
  'backend/Cent.Preview/Program.cs':'Confirmed local SQLite path and DateTimeOffset UTC tick conversion; startup would write and was never run.',
  'backend/Cent.Preview/README.md':'Development-only preview database documentation.',
  'backend/Cent.Curriculum/README.md':'Bundled sources and authoring workflow; no generators executed.',
  'backend/Cent.Curriculum/SourceReader.cs':'Extraction, sanitization, SVG processing and original-HTML preservation.',
  'backend/Flashcards.Infrastructure/Persistence/FlashcardsDbContext.cs':'Separate Lerona context identified by source only; no Lerona records read.',
};
writeJson('audit/source_architecture_evidence.json',{datasetLabel:LABEL,files:Object.entries(inspectedFiles).map(([file,description])=>({file,description,sha256:sha(fs.readFileSync(path.join(ROOT,file)))})),configurationLocations:['backend/Flashcards.Api/appsettings*.json (values not exported)','environment ConnectionStrings__CentDb / ConnectionStrings__FlashcardsDb (not used)','Cent.Preview computes its local path without using those SQL Server connections'],databaseProvider:'SQLite; node:sqlite readOnly=true',runtimeVersions:{node:process.version,sqlite:'Queried provider was node:sqlite; no SQL Server provider loaded.'}},'Source architecture evidence and hashes; no configuration file contents or credentials included.');
const coordinationObservation={description:'An earlier readOnly=true connection without immutable mode was followed by appearance of a zero-byte preview.db-wal and a 32768-byte preview.db-shm. SQLite read-only opens can create coordination files. No SQL write statements were issued. These files were not deleted or packaged. Final extraction and verification use mode=ro&immutable=1 after checking that WAL is empty, and compare fingerprints before/after.',mainDatabaseSha256BeforeAndAfterEarlierOpen:'ecaef99c2f4e646cc475abd67d677f0e83c6d98327a1e1352895f0db1b180070',mainDatabaseModifiedUtcBeforeAndAfterEarlierOpen:'2026-11-19T13:47:54.312Z',walBytesObserved:0,shmBytesObserved:32768};
writeJson('audit/read_only_evidence.json',{datasetLabel:LABEL,databasePath:DB,readOnlyOpen:true,immutableUri:true,nonemptyWalRejected:true,queryOnlyPragma:true,allowExtension:false,singleReadTransaction:true,databaseAndSidecarsUnchanged:databaseUnchanged,before,after,earlierCoordinationFileObservation:coordinationObservation,statementPolicy:'Only SELECTs of explicit educational columns and metadata, read-only PRAGMAs, BEGIN, and ROLLBACK. No application host or DbContext startup.',rowCounts:counts,...privacy},'Database and sidecar fingerprints before/after extraction, earlier coordination-file observation, and read-only methodology.');

const tableLines=Object.entries(counts).map(([t,n])=>`| ${t} | ${n} |`).join('\n');
const subjectLines=subjectCounts.map(c=>`| ${c.subjectCode??'UNASSIGNED'} | ${c.chapters} | ${c.topics} | ${c.lessons} | ${c.sourceSections} | ${c.sourceExercises} | ${c.questions} |`).join('\n');
const comparisonLines=comparisonSummary.map(c=>`| ${c.kind} | ${c.bundled} | ${c.database} | ${c.exactSemanticMatches} | ${c.differing} | ${c.missingInDatabase} | ${c.databaseOnly} |`).join('\n');
const issuesByCode=Object.fromEntries([...new Set(issueRows.map(x=>x.code))].sort().map(code=>[code,issueRows.filter(x=>x.code===code).length]));
const severityCounts=Object.fromEntries(['error','warning','info'].map(s=>[s,issueRows.filter(x=>x.severity===s).length]));
const missingFields={};
for(const i of issueRows.filter(i=>['EMPTY_CONTENT_FIELD','MISSING_ENGLISH_TOPIC_TITLE'].includes(i.code))){const k=i.table+'.'+i.field;missingFields[k]=(missingFields[k]??0)+1;}
const allRawMatch=sourceChecks.every(s=>s.checksumMatches&&s.rawHtmlMatchesDatabase);
const summary={subjects:data.cent_courses.length,chapters:data.cent_chapters.length,topics:data.cent_topics.length,lessons:data.cent_documents.length,sourceBasedLessons:data.cent_documents.filter(d=>d.SourceId!==null).length,imageBasedLessons:data.cent_documents.filter(d=>d.SourceId===null).length,sources:data.cent_sources.length,sourceSections:data.cent_source_sections.length,sourceExercises:data.cent_source_exercises.length,questions:data.cent_questions.length,answerChoices:answerChoices.length,storedReusableMockExamDefinitions:0,standaloneEditorialStudyNotes:'not separately modelled',personalNotes:'excluded and not counted',curriculumPresent,curriculumMatches};
writeFile('CONTENT_INVENTORY.md',`# CEnT-S educational content inventory — DEVELOPMENT DATA

Generated: ${new Date().toISOString()}. This is a development preview export for independent review, not a production inventory or academic rewrite.

## Database and sources inspected

The only database opened was \`${DB}\` (SQLite), explicitly confirmed by the requester. The final export used Node's built-in SQLite provider with \`readOnly: true\`, URI \`mode=ro&immutable=1\`, extension loading disabled, \`PRAGMA query_only=ON\`, and one read transaction across all content tables. Immutable mode is permitted only after rejecting any nonempty WAL, and before/after fingerprints detect source changes. The application was not started. No SQL Server, remote service, production database, or Lerona fallback connection was used. Database and sidecar bytes/mtime unchanged across final extraction: **${databaseUnchanged}**. An earlier non-immutable read-only open produced SQLite coordination sidecars (empty WAL and shared memory); main database bytes and modification time remained unchanged. This observation is retained in [read-only evidence](audit/read_only_evidence.json).

\`CentDbContext\` owns the \`cent_*\` entities. \`Cent.Preview\` replaces its SQL Server provider with SQLite and computes this local path; its startup can create/upgrade/seed data and was deliberately not executed. In the shared host, \`ConnectionStrings:CentDb\` falls back to \`ConnectionStrings:FlashcardsDb\`; the design-time factory can also fall back to LocalDB CentDev. Those paths were identified by reading code only. Lerona uses the separate \`FlashcardsDbContext\`. No Lerona records were read or exported.

The repository bundle is \`backend/Cent.Module/Content/curriculum.json\`, SHA-256 \`${bundleHash}\`, plus ${actualHtmlFiles.length} original HTML files under \`Content/Sources\` and ${walk(path.join(CONTENT,'Review')).length} existing derived review files under \`Content/Review\`. JSON and HTML are embedded resources at build time. The package and existing review files are included byte-for-byte. Compiled assemblies and any deployed content were not inspected.

## Totals and definitions

There are **${summary.subjects} subjects, ${summary.chapters} chapters, ${summary.topics} topics, ${summary.lessons} lessons/documents, ${summary.questions} scored-bank questions and 0 reusable stored mock-exam definitions**. The lessons comprise ${summary.sourceBasedLessons} source-based documents and ${summary.imageBasedLessons} image-based documents; source files, source sections and documents are different representations and must not be summed as separate lessons. There are ${summary.sourceExercises} source exercises, including open-response and quarantined material; these overlap with scored questions and are not an additional disjoint bank.

| Content table | Exported records |
| --- | ---: |
${tableLines}

All rows from these educational tables were exported, including publication/activation flags. No row limit or published-only filter was applied.

## Counts by subject

| Subject | Chapters | Topics | Lessons | Source sections | Source exercises | Questions |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
${subjectLines}

Unassigned content has no stored subject relationship and was not assigned from title or appearance. Topic counts, exact IDs, question difficulty counts and lesson links are in [counts by topic](derived/counts_by_topic.json). ${noTopicSections.length} source sections and ${noTopicExercises.length} source exercises lack a TopicId; introductions, navigation and review sections may intentionally be unassigned. ${zeroQuestionTopics.length} topics have no active scored questions. Full lists are in [unmapped content](derived/unmapped_content.json).

## Database versus bundled curriculum

Verdict: **${curriculumMatches?'The preview contains the complete bundled curriculum, with matching importer-managed values and matching original HTML; it is not merely a subset.':curriculumPresent?'All bundled curriculum identities are present, but field or source differences exist; inspect the comparison before treating it as identical.':'The preview contains only a subset of the bundled curriculum; see exact missing identities in comparison.json.'}** This does not establish coverage of the official syllabus or production content.

| Content | Bundle | Database | Matching | Differing | Missing from DB | DB-only |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
${comparisonLines}

Raw bundled HTML files all match expected checksums and database OriginalHtml: **${allRawMatch}**. Current bundle SHA is recorded in curriculum import history: **${comparison.bundleImportRecorded}**. Import history has ${data.cent_curriculum_imports.length} records; history alone was not used as proof of completeness. Field-level comparisons independently reproduced the importer mappings and deterministic IDs. JSON option escaping/serialization-only differences: ${formattingOnly.length}; original strings are preserved.

The database-only trainings/documents are included as additional development content. Their repeated titles are duplicate candidates, not evidence that they should be deleted.

## Educational text, choices, versions and assets

Full OriginalHtml/PlainText, source-section Text/Html, exercise Text/Solution, question Text/OptionsJson/CorrectAnswer/Explanation/FigureSvg, relationship IDs, difficulty, origin and flags are preserved. The choice index is zero-based. Ordered parsed choices are also supplied in [answer choices](derived/answer_choices.json). Correct answers have not been re-evaluated academically.

Study-note prose is part of sources/sections. There is no separate editorial study-note table in this model. \`cent_notes\` contains personal user annotations and is excluded entirely. The ${summary.imageBasedLessons} image-only lessons retain all ${data.cent_document_pages.length} stored page BLOBs as checksummed assets; they have no stored machine-readable lesson prose. Original uploaded PDFs are not retained by the application and cannot be exported. No OCR was performed.

Mock exams and diagnostics are generated at runtime with up to 20 questions using subject/topic sampling. No reusable exam-template or question-group table was found. [Mock exam metadata](derived/mock_exams.json) documents source-inspected rules; [question groupings](derived/question_groupings.json) are explicitly derived topic views. Private learner attempts and their snapshots were neither read nor counted.

Publication/activation flags and educational content creation/import times are retained. Package hashes, source checksums and import keys provide provenance. This is a current-state export; no comprehensive editorial revision history or historical question versions are available outside the excluded user-attempt snapshots. Exercise-level Difficulty and FigureSvg exist in the bundle but not in the source-exercise database entity; the bundle preserves them.

All original HTML, inline SVG and WebP BLOBs are preserved. [Asset references](audit/asset_references.json) records HTML/SVG attributes and CSS URLs. External resources were not fetched; font/CDN/site dependencies may need network access when opened. Markup was not executed. This scan is static and does not resolve URLs constructed by JavaScript or every possible CSS escape.

## Completeness and privacy

The educational database export is complete for the 12 explicitly allowlisted tables and fields listed in [schema](audit/schema.json). Missing or differing bundle content, empty fields, broken links and duplicate candidates are detailed in [issues](CONTENT_ISSUES.md). All source rows were exported; no corrections or deduplication were applied. Assets are retained as original bytes.

All user, authentication, payment, purchase, entitlement, personal-note, progress, learner attempt/result/analysis and actor audit tables were excluded without querying their rows. No whole database, backup, credentials, configuration file contents or Lerona data are packaged. See [privacy scope](audit/privacy_scope.json).

## Package navigation and validation

- [CONTENT_SCHEMA.md](CONTENT_SCHEMA.md): entity meanings, keys, relationships and value encoding.
- [CONTENT_ISSUES.md](CONTENT_ISSUES.md): limitations and mechanical findings, with exact record lists in JSON.
- \`database/\`: one JSON file per educational content type.
- \`bundled/\`: original curriculum JSON, HTML and existing review files.
- \`assets/\`: database source HTML and original document-page BLOBs.
- \`derived/\`: hierarchy, counts, choices, coverage and source-inspected exam metadata.
- \`audit/\`: comparison, issues, schema, privacy and read-only evidence.
- [EXPORT_MANIFEST.json](EXPORT_MANIFEST.json): file descriptions, counts, byte sizes and SHA-256 values.

The ZIP contains this directory. The manifest hashes every payload file except itself, to avoid a circular digest; the ZIP has a separate SHA-256 sidecar. Archive entries are verified against the manifest after packaging.
`, 'Main development educational inventory report.');

const schemaDetails=Object.entries(schema).map(([table,s])=>`### ${table} → database/${filesByTable[table]}.json

Primary key: ${s.primaryKey.join(' + ')}. Rows: ${s.count}.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
${s.columns.map(c=>`| ${c.name} | ${c.type} | ${c.notnull?'no':'yes'} | ${c.pk} |`).join('\n')}

Foreign keys: ${s.foreignKeys.length?s.foreignKeys.map(f=>`${f.from} → ${f.table}.${f.to}`).join('; '):'none declared'}.
`).join('\n');
writeFile('CONTENT_SCHEMA.md',`# CEnT-S content schema — DEVELOPMENT DATA

This describes the actual confirmed SQLite preview and source models. Actual content-only table metadata, defaults and index definitions are in [audit/schema.json](audit/schema.json).

## Entity meanings and relationships

| Audit concept | Stored representation | Relationship |
| --- | --- | --- |
| Subject | CentCourse / cent_courses | Code is stable key; optional TrainingId |
| Chapter | CentChapter / cent_chapters | CourseCode + SourceId |
| Topic | CentTopic / cent_topics | ChapterId + CourseCode |
| Sale/access grouping | CentTraining / cent_trainings | Documents belong to TrainingId; only educational metadata exported |
| Lesson | CentDocument / cent_documents | TrainingId and optional SourceId |
| Source lesson text | CentSource / cent_sources | CourseCode; full OriginalHtml and PlainText |
| Lesson section / virtual page | CentSourceSection / cent_source_sections | SourceId; optional TopicId; ordered Text/Html |
| Image lesson page | CentDocumentPage / cent_document_pages | Composite DocumentId + Number; Image BLOB |
| Image lesson outline | CentSection / cent_document_sections | DocumentId + StartPage |
| Original exercise | CentSourceExercise / cent_source_exercises | SourceSectionId, optional TopicId; original answer and Solution |
| Scored question | CentQuestion / cent_questions | Subject; optional TopicId, SourceSectionId, SourceExerciseId |
| Answer choices | OptionsJson on questions/exercises | Ordered array of strings; no stored choice IDs |
| Correct answer | CorrectAnswer / OriginalCorrectAnswer | Zero-based option index; original exercise answer can be null |
| Explanation | Explanation / Solution | Full stored prose |
| Editorial study notes | No separate entity | Teaching prose is in sources and source sections |
| Personal notes | CentNote / cent_notes | User-owned; excluded without reading rows |
| Question groups | No separate stored entity | Runtime grouping by Subject and TopicId/Topic; derived topic groups supplied |
| Reusable mock exams | No separate stored entity | Runtime question sampling; no reusable exam definition records |
| Learner mock attempts | CentAttempt + CentAttemptQuestion | User-owned; excluded, including historic question snapshots |
| Import provenance | CentCurriculumImport | PackageHash and aggregate counts/import time; no actor data |

Source exercises and scored questions are related but have different purposes. Open-response and quarantined source exercises can exist without scored questions. Source sections can intentionally have no topic. An image-only lesson does not have a SourceId and uses page images and document sections instead of source-section virtual pages. No official syllabus alignment entity or external question bank integration was identified in these models.

## Export encoding

All generated text files use UTF-8 without a BOM. Original bundled files are copied without byte transformation. Each database JSON uses the envelope \`{ datasetLabel, source, recordCount, records }\`. Column names, stored GUID strings, import keys, option JSON strings, nulls and flags are preserved. GUID comparisons are case-insensitive; no exported IDs were changed. Deterministic bundled GUID derivation is documented in [comparison](audit/comparison.json).

SQLite integer flags remain 0/1. Integers outside JavaScript's safe range are decimal strings, preserving every digit. In particular, preview DateTimeOffset values are .NET UTC ticks (100 ns since 0001-01-01), exported as strings; they are not Unix milliseconds. To derive Unix milliseconds use \`(BigInt(ticks) - 621355968000000000n) / 10000n\`. No timestamp rounding was applied to the source fields.

The page Image field becomes \`{ encoding: "external-file", path, bytes, sha256, mediaType }\`; the exact BLOB is stored at that path, relative to the package root. All other database fields remain direct stored values. Database source OriginalHtml is additionally written to a per-source UTF-8 HTML file.

Derived answer choices use the natural composite identity \`parentTable + parentId + position\`; the database has no independent choice IDs. The derived boolean \`correct\` follows the stored index and can be null for original exercises with no answer index. It does not validate the answer academically.

## Versions and publication

Trainings/documents retain Published, questions/courses retain Active, exercises retain Status/ReviewNote, questions retain Origin/ImportKey/FigureSvg, and sources retain Sha256/ImportedAt. CurriculumImports retains package hashes and import counts. A package import receipt does not contain the old package itself. No separate full editorial revision history or question version column exists. Personal-note Revision is not an editorial content version and was excluded with its user-owned table.

## Bundled content outside the database

\`Content/curriculum.json\` contains sources, chapters, topics, sections, exercises, questions and existing review findings. The HTML files under Sources contain original formatting, inline diagrams, teaching text and references. SourceReader creates simplified/sanitized section HTML and plain text; those derivatives can omit visual elements which remain in original HTML. Source-exercise DTO Difficulty and FigureSvg have no corresponding database columns and remain available in the bundled JSON. Review text/inventory is a derived authoring output, not another independent set of lessons.

\`Cent.Curriculum/SourceReader.cs\`, QuestionAuthoring.cs, OtherAuthoring.cs and PhysicsAuthoring.cs implement extraction/authored question generation; they were not run. \`Cent.Module.csproj\` embeds the source package. Original upload PDFs and original author-supplied archives are not persisted by these content models. No external asset download was attempted.

## Actual exported tables

${schemaDetails}
`, 'Actual educational schema, entity meanings, relationships, value encodings and exclusions.');
const duplicateLines=duplicates.map(g=>`| ${g.table} | ${g.criterion} | ${g.count} | ${g.recordIds.map(x=>typeof x==='string'?x:JSON.stringify(x)).join(', ')} |`).join('\n');
const emptyFieldLines=Object.entries(missingFields).map(([k,n])=>`| ${k} | ${n} |`).join('\n');
writeFile('CONTENT_ISSUES.md',`# CEnT-S export issues — DEVELOPMENT DATA

This is a mechanical inventory, not an academic or syllabus judgment. No educational text, keys, statuses or relationships were corrected. Exact affected identities and details are in [audit/issues.json](audit/issues.json).

## Completeness and comparison

Full bundled identities present: **${curriculumPresent}**. All importer-managed values and raw HTML match: **${curriculumMatches}**. Missing bundled database identities: **${comparisonSummary.reduce((n,x)=>n+x.missingInDatabase,0)}**. Differing database records: **${comparisonSummary.reduce((n,x)=>n+x.differing,0)}**. HTML/checksum mismatches: **${sourceChecks.filter(x=>!x.checksumMatches||!x.rawHtmlMatchesDatabase).length}**. Inspect [comparison](audit/comparison.json) for exact fields and IDs, including any database-only records. Question option JSON serialization-only differences are listed separately and are not content differences.

## Relationships and incomplete content

- Broken declared educational foreign keys: **${brokenRelationships.length}**. All declared content foreign keys were checked against the exported parent records without reading excluded tables.
- Cross-subject semantic link checks and document page/section bounds are included in the issue list. This is not a whole-database integrity or corruption check.
- Questions whose TopicId differs from their linked original exercise: **${issueRows.filter(i=>i.code==='QUESTION_EXERCISE_TOPIC_MISMATCH').length}**. Both mappings match the bundle, so this is not database drift or a broken foreign key. It may reflect intentional retagging; exact question/exercise IDs and topic titles are preserved for editorial review.
- Source sections without TopicId: **${noTopicSections.length}**. Source exercises without TopicId: **${noTopicExercises.length}**. These nullable mappings can be intentional; exact IDs are supplied.
- Topics without active scored questions: **${zeroQuestionTopics.length}**. The complete source corpus can therefore still have assessment coverage gaps.
- Image-only lessons: **${summary.imageBasedLessons}**, with **${data.cent_document_pages.length}** exported images. They have no stored machine-readable full lesson text or original PDF. No OCR was invented.
- Standalone editorial study notes: no separate table was found; full teaching text is preserved in sources/sections. Personal learner notes were intentionally excluded.
- Reusable stored mock exam templates: **0**. Runtime sampling rules are supplied. Private attempts/results, including any historical snapshots, are outside scope and were not read or counted.

### Empty or missing prose/labels

| Field | Affected records |
| --- | ---: |
${emptyFieldLines||'| None found in the checked fields | 0 |'}

Optional missing English titles and empty section text are reported separately from invalid question fields in the detailed issue records. Source text remains available in its full original HTML even where extraction produced empty derivatives.

## Source exercises and prior authoring findings

Source exercise status counts: \`${JSON.stringify(exerciseStatusCounts)}\`. Missing original answer indices may correspond to legitimate open-response exercises. Scored-bank validation checks JSON parsing, at least two choices, non-empty/unique choices, answer bounds, question/explanation text and recognized difficulty; it does not prove correct answers.

The bundle already contains ${bundle.findings.length} authoring findings, preserved unchanged below. They describe earlier authoring decisions; this audit applied no corrections:

${bundle.findings.map(f=>`- \`${f.key}\` — **${f.action}**: ${f.reason}`).join('\n')}

## Duplicate candidates

There are **${duplicates.length} candidate groups**, potentially overlapping. Criteria are whitespace-normalized exact statement + ordered options, repeated lesson/training titles, identical ordered image-page hashes, or identical source HTML. Repeated titles alone do not establish identical lesson text; ordered page hashes establish byte-identical image sets. No fuzzy or academic-equivalence detection was performed and no records were removed.

| Table | Criterion | Records | IDs |
| --- | --- | ---: | --- |
${duplicateLines||'| None | — | 0 | — |'}

See [duplicate candidates](audit/duplicate_candidates.json) for machine-readable groups/fingerprints. Extra repeated development lessons may be fixture material; their provenance is not established by this read-only inspection.

## Assets and unavailable sources

Static reference statuses: \`${JSON.stringify(assetStatusCounts)}\`. All local page BLOBs, database OriginalHtml, bundled HTML and inline SVGs are preserved. References remain exactly as stored. External resources were not fetched or verified. Missing local files, if any, are listed as UNRESOLVED_ASSET_REFERENCE in issues.json. Static scanning covers common HTML/SVG attributes and CSS URLs; dynamically constructed references and all possible CSS encodings are outside the check.

Original upload PDFs, original input ZIP/RAR archives, unpublished files outside the confirmed repository bundle, production records, deployed assembly resources and excluded learner snapshots were not available within the authorized content scope. The JSON/HTML bundle is self-contained for its text and inline figures, but original HTML can retain network-dependent fonts or styles.

## Provenance and operating limits

- **DEVELOPMENT DATA** does not establish production parity or official CEnT-S syllabus coverage.
- This snapshot includes all allowlisted educational rows. It is not a whole-database backup, and private or commercial records are excluded by design.
- Existing import receipts and content hashes are retained; comprehensive editorial revision history is not stored in these content entities.
- Database/sidecar fingerprint unchanged during extraction: **${databaseUnchanged}**. A single read transaction provided a consistent content snapshot; the audit did not stop any running process or disable external writers.
- An earlier read-only open without immutable mode was followed by creation of a zero-byte WAL and 32768-byte SQLite shared-memory coordination file. The main database SHA-256 and modification time did not change; no SQL write statements, record updates or schema changes were issued. Coordination files were not deleted or included in the export. The final pass uses an immutable read-only URI, rejects a nonempty WAL and verifies unchanged fingerprints. Details are retained in read_only_evidence.json.
- Full prose and raw markup were exported without execution. Opening original HTML may request its external dependencies; the audit did not execute it.

## Mechanical finding totals

Severity counts: \`${JSON.stringify(severityCounts)}\`. Informational records identify expected nullable fields, open-response items or image-only lessons; they are not necessarily defects.

| Finding code | Records |
| --- | ---: |
${Object.entries(issuesByCode).map(([code,n])=>`| ${code} | ${n} |`).join('\n')||'| None | 0 |'}
`, 'Export limitations, relationship checks, missing fields, duplicate candidates and unchanged prior findings.');
writeFile('EXPORT_METHOD.mjs',fs.readFileSync(fileURLToPath(import.meta.url)),'Standalone read-only export utility, supplied for reproducibility; never starts application code.');
writeFile('VERIFY_METHOD.mjs',fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)),'verify-development.mjs')),'Independent payload verifier supplied for reproducibility; only confirmed SQLite content is queried.');
// The manifest excludes only itself to avoid a circular hash. ZIP hash is external.
const manifestFiles=walk(OUT).map(p=>{
  const rel=path.relative(OUT,p).replaceAll('\\','/');
  const bytes=fs.readFileSync(p);
  return {path:rel,bytes:bytes.length,sha256:sha(bytes),...fileMeta.get(rel)};
});
const manifest={formatVersion:1,datasetLabel:LABEL,generatedUtc:new Date().toISOString(),authorizedDatabase:{provider:'SQLite',path:DB,environment:'DEVELOPMENT',noFallback:true},bundleSha256:bundleHash,summary,recordCountsByDatabaseTable:counts,bundledRecordCounts:Object.fromEntries(Object.entries(bundle).map(([k,v])=>[k,v.length])),subjectCounts,bankDistributions,exerciseStatusCounts,assetReferenceStatusCounts:assetStatusCounts,issueCountsBySeverity:severityCounts,issueCountsByCode:issuesByCode,duplicateCandidateGroups:duplicates.length,completeness:{educationalTableExportComplete:true,bundledCurriculumIdentitiesPresent:curriculumPresent,importerManagedValuesAndHtmlMatch:curriculumMatches,allBundledHtmlChecksumsMatch:sourceChecks.every(s=>s.checksumMatches),databaseUnchanged,publicationFiltersApplied:false,rowLimitApplied:false,limitationsFile:'CONTENT_ISSUES.md'},privacy,fileCountExcludingManifest:manifestFiles.length,manifestDigestPolicy:'files contains every package file except EXPORT_MANIFEST.json. ZIP has an external .sha256 sidecar; archive integrity is checked separately.',files:manifestFiles};
writeJson('EXPORT_MANIFEST.json',manifest,'Export scope, counts, limitations and SHA-256 file inventory.');
console.log(JSON.stringify({outputDirectory:OUT,summary,comparisonSummary,severityCounts,issuesByCode,duplicateCandidateGroups:duplicates.length,assetStatusCounts,databaseUnchanged,filesIncludingManifest:manifestFiles.length+1},null,2));
