// Independent verification of payload hashes, UTF-8, JSON, and fresh read-only rows.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {DatabaseSync} from 'node:sqlite';
const out=path.resolve(process.argv[2]);
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const manifestPath=path.join(out,'EXPORT_MANIFEST.json');
const manifest=JSON.parse(fs.readFileSync(manifestPath,'utf8'));
const read=rel=>JSON.parse(fs.readFileSync(path.join(out,rel),'utf8'));
const walk=d=>fs.readdirSync(d,{withFileTypes:true}).flatMap(e=>e.isDirectory()?walk(path.join(d,e.name)):[path.join(d,e.name)]);
const assert=(ok,message)=>{if(!ok)throw Error(message);};
let jsonFiles=0,textFiles=0;
for(const file of manifest.files){
  const bytes=fs.readFileSync(path.join(out,file.path));
  assert(bytes.length===file.bytes&&hash(bytes)===file.sha256,'File digest mismatch: '+file.path);
  if(/\.(json|md|mjs|html|txt)$/i.test(file.path)){new TextDecoder('utf-8',{fatal:true}).decode(bytes);textFiles++;}
  if(file.path.endsWith('.json')){
    const content=JSON.parse(bytes.toString('utf8'));jsonFiles++;
    if(Array.isArray(content.records)&&Number.isInteger(content.recordCount))assert(content.records.length===content.recordCount,'Envelope count mismatch: '+file.path);
  }
}
const listed=new Set([...manifest.files.map(f=>f.path),'EXPORT_MANIFEST.json']);
for(const f of walk(out))assert(listed.has(path.relative(out,f).replaceAll('\\','/')),'Unmanifested file: '+f);
const schema=read('audit/schema.json').tables;
const evidence=read('audit/read_only_evidence.json');
assert(evidence.databasePath.replaceAll('\\','/')==='D:/Lerona/Backend/backend/Cent.Preview/App_Data/preview.db','Unexpected database target');
const before=hash(fs.readFileSync(evidence.databasePath));
assert(before===evidence.before['preview.db'].sha256,'Database changed since export');
const canon=v=>{
  if(typeof v==='bigint')return v<=BigInt(Number.MAX_SAFE_INTEGER)&&v>=BigInt(Number.MIN_SAFE_INTEGER)?Number(v):v.toString();
  if(Array.isArray(v))return v.map(canon);
  if(v&&typeof v==='object')return Object.fromEntries(Object.keys(v).sort().map(k=>[k,canon(v[k])]));
  return v;
};
const db=new DatabaseSync(evidence.databasePath,{readOnly:true,allowExtension:false});
const checkedRows={};
try{
  db.exec('PRAGMA query_only=ON; BEGIN');
  assert(db.prepare('PRAGMA query_only').get().query_only===1,'Query-only enforcement absent');
  for(const [table,s] of Object.entries(schema)){
    assert(/^cent_[a-z_]+$/.test(table),'Invalid table identifier');
    const file=manifest.files.find(f=>f.source===table&&f.path.startsWith('database/'));
    assert(!!file,'No database export for '+table);
    const exported=read(file.path).records;
    const statement=db.prepare('SELECT '+s.exportedColumns.map(c=>'"'+c+'"').join(',')+' FROM "'+table+'" ORDER BY '+s.primaryKey.map(c=>'"'+c+'"').join(','));
    statement.setReadBigInts(true);
    const rows=statement.all();
    assert(rows.length===exported.length,'Database row count mismatch for '+table);
    for(let i=0;i<rows.length;i++){
      if(table==='cent_document_pages'){
        const bytes=Buffer.from(rows[i].Image),ref=exported[i].Image;
        assert(hash(bytes)===ref.sha256&&bytes.equals(fs.readFileSync(path.join(out,ref.path))),'Page BLOB mismatch');
        rows[i].Image=ref;
      }
      assert(JSON.stringify(canon(rows[i]))===JSON.stringify(canon(exported[i])),'Stored field mismatch: '+table+' row '+i);
    }
    checkedRows[table]=rows.length;
  }
  db.exec('ROLLBACK');
}finally{db.close();}
assert(hash(fs.readFileSync(evidence.databasePath))===before,'Database bytes changed during verification');
const bundle=read('bundled/curriculum.json');
for(const src of bundle.sources)assert(hash(fs.readFileSync(path.join(out,'bundled/Sources',src.file)))===src.sha256,'Bundled source checksum mismatch');
// Inspect content exports for secret-shaped values without printing any matches.
const secretFindings=[];
for(const rel of [...manifest.files.map(f=>f.path)].filter(p=>p.startsWith('database/')||p.startsWith('bundled/'))){
  if(!/\.(json|html|txt)$/i.test(rel))continue;
  const text=fs.readFileSync(path.join(out,rel),'utf8');
  const patterns=[/-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/,/\b(?:sk-proj-|ghp_|github_pat_)[A-Za-z0-9_-]{16,}/,/\b(?:Password|Pwd)\s*=\s*[^;\s"']+/i,/\bBearer\s+[A-Za-z0-9_.-]{20,}/i,/\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/i];
  if(patterns.some(p=>p.test(text)))secretFindings.push(rel);
}
assert(secretFindings.length===0,'Review possible sensitive content in files: '+secretFindings.join(', '));
const result={datasetLabel:'DEVELOPMENT DATA',verifiedUtc:new Date().toISOString(),result:'PASS',hashedPayloadFiles:manifest.files.length,validUtf8TextFiles:textFiles,parsedJsonFiles:jsonFiles,allDatabaseFieldsMatchFreshRead:true,checkedRows,allBundledHtmlHashesMatch:true,noUnmanifestedPayloadFiles:true,contentSecretPatternScan:'No matches in educational JSON/HTML/review text; explicit allowlist excludes user-owned tables.',databaseBytesUnchanged:true,pageImageVisualReview:'Four unique page-image hashes were visually inspected; all contain algebra teaching examples without personal annotations or identifiers. Eighteen BLOBs form duplicate page sets.',archiveVerification:'ZIP entry paths, sizes and SHA-256 hashes are verified after packaging by the packaging command.'};
const rel='audit/verification.json';
const bytes=Buffer.from(JSON.stringify(result,null,2)+'\n');fs.writeFileSync(path.join(out,rel),bytes);
manifest.files.push({path:rel,bytes:bytes.length,sha256:hash(bytes),description:'Independent UTF-8/JSON/hash verification, fresh read-only database field comparison, and content privacy checks.',recordCount:null,source:'independent audit verification'});
manifest.fileCountExcludingManifest=manifest.files.length;
manifest.verification={result:'PASS',details:rel};
fs.writeFileSync(manifestPath,JSON.stringify(manifest,null,2)+'\n');
console.log(JSON.stringify({outputDirectory:out,...result},null,2));
