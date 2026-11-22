import fs from 'node:fs'
import path from 'node:path'
import assert from 'node:assert/strict'

const num = s => Number(s.replaceAll('−','-'))
const factorial = n => n < 2 ? 1 : n * factorial(n-1)
// These are independently expressed calculations from the issued prompts, not a
// replay of QuestionAuthoring's answer generator. A match checks a key, not rights.
const rules = [
[/^Which is the greatest number: −(\d+),/, a=>-a, 'Ordering negative integers'],
[/^What is the greatest common divisor of (\d+) and (\d+)\?$/, (a,b)=>{while(b)[a,b]=[b,a%b];return a}, 'Euclidean algorithm'],
[/^A price of (\d+) increases by (\d+)%/, (a,b)=>a*(1+b/100), 'Original price times 1+p/100'],
[/^Evaluate 2\^(\d+) × 2\^(\d+)\.$/, (a,b)=>2**(a+b), 'Product of powers'],
[/^Evaluate √(\d+)\.$/, a=>Math.sqrt(a), 'Nonnegative principal root'],
[/^Evaluate (\d+) \+ 3 × 4\^2\.$/, a=>a+48, 'Order of operations'],
[/^If x=(\d+), evaluate 2x\^2 − 3x \+ 1\.$/, a=>2*a*a-3*a+1, 'Polynomial substitution'],
[/^Solve 3x \+ (\d+) = (\d+)\.$/, (a,b)=>(b-a)/3, 'Linear equation'],
[/^Solve x\+y=(\d+) and x−y=2\. What is x\?$/, a=>(a+2)/2, 'Elimination of y'],
[/^What is the slope of the line through \(0,1\) and \((\d+),(\d+)\)\?$/, (x,y)=>(y-1)/x, 'Coordinate slope'],
[/^What is the distance between \(0,0\) and \((\d+),(\d+)\)\?$/, (x,y)=>Math.hypot(x,y), 'Coordinate norm'],
[/^What is the distance from \((\d+),(\d+)\) to the line y=(\d+)\?$/, (_x,y,b)=>Math.abs(y-b), 'Distance to horizontal line'],
[/^What is the radius of \(x−1\)²\+\(y\+2\)²=(\d+)\?$/, a=>Math.sqrt(a), 'Circle radius from radius squared'],
[/^A code has one letter chosen from (\d+) letters and one digit chosen from (\d+) digits\./, (a,b)=>a*b, 'Product rule'],
[/^In how many ways can (\d+) distinct books be arranged in a row\?$/, a=>factorial(a), 'Permutations'],
[/^How many unordered pairs can be selected from (\d+) people\?$/, n=>n*(n-1)/2, 'Combinations'],
[/^A value occurs (\d+) times in a dataset of (\d+) observations\./, (a,b)=>a/b, 'Relative frequency'],
[/^A pie-chart category represents (\d+)%/, p=>p*3.6, 'Proportion of 360 degrees'],
[/^Find the arithmetic mean of (\d+), (\d+), (\d+)\.$/, (a,b,c)=>(a+b+c)/3, 'Arithmetic mean'],
[/^Find the range of (\d+), (\d+), (\d+)\.$/, (a,b,c)=>Math.max(a,b,c)-Math.min(a,b,c), 'Maximum minus minimum'],
[/^Two supplementary angles are (\d+)°/, a=>180-a, 'Supplementary angle sum'],
[/^A triangle has base (\d+) and perpendicular height (\d+)\./, (a,b)=>a*b/2, 'Triangle area'],
[/^A rectangular cuboid has dimensions (\d+), (\d+) and (\d+)\./, (a,b,c)=>a*b*c, 'Cuboid volume'],
[/^The side lengths of two similar figures have scale factor (\d+)\./, a=>a*a, 'Area similarity scaling'],
[/^Which quadrant contains an angle of (\d+)°\?$/, a=>Math.floor((a%360)/90)+1, 'Quadrant intervals'],
[/^What is the maximum value of f\(x\)=(\d+)sin x\+1\?$/, a=>a+1, 'Maximum of positive-amplitude sine'],
[/^Find the x-coordinate of the vertex of y=x²−(\d+)x\+1\.$/, b=>b/2, 'Vertex -b/2a'],
[/^Solve 2\^x=(\d+)\.$/, a=>Math.log2(a), 'Inverse exponent'],
[/^Evaluate log_(\d+)\((\d+)\)\.$/, (a,b)=>Math.log(b)/Math.log(a), 'Logarithm definition'],
[/^If f\(x\)=2x\+1 and g\(x\)=x², find f\(g\((\d+)\)\)\.$/, a=>2*a*a+1, 'Function composition'],
[/^Convert (\d+) kilometres into metres\.$/, a=>1000*a, 'SI length conversion'],
[/^A ruler's smallest division is (\d+(?:\.\d+)?) cm\./, a=>10*a, 'Centimetres to millimetres'],
[/^Perpendicular vectors have magnitudes (\d+) and (\d+)\./, (a,b)=>Math.hypot(a,b), 'Pythagorean vector sum'],
[/^A runner completes a (\d+) m trip in (\d+) s\./, (a,b)=>a/b, 'Distance over elapsed time'],
[/^Uniform motion starts at x₀=(\d+) m with velocity (\d+) m\/s\. Find x after (\d+) s/, (x,v,t)=>x+v*t, 'Uniform motion'],
[/^Velocity increases from (\d+) to (\d+) m\/s in (\d+) s\./, (u,v,t)=>(v-u)/t, 'Average acceleration'],
[/^A velocity–time graph is horizontal at (\d+) m\/s from t=0 to t=(\d+) s\./, (v,t)=>v*t, 'Signed area under velocity graph'],
[/^Uniform circular motion has speed (\d+) m\/s and radius (\d+) m\./, (v,r)=>v*v/r, 'Centripetal acceleration'],
[/^Two vehicles move in the same direction at (\d+) and (\d+) m\/s\./, (v,u)=>v-u, 'Relative velocity'],
[/^A constant (\d+) N force acts parallel to a displacement of (\d+) m\./, (f,d)=>f*d, 'Work of parallel force'],
[/^A (\d+) kg object is raised vertically by (\d+) m at constant speed\./, (m,h)=>-m*10*h, 'Negative work of gravity, stated g=10'],
[/^A (\d+) kg object initially at rest reaches (\d+) m\/s\./, (m,v)=>m*v*v/2, 'Work-energy theorem'],
[/^A machine performs (\d+) J of work in (\d+) s\./, (w,t)=>w/t, 'Average power'],
[/^A (\d+) kg object moves at (\d+) m\/s\. What is its kinetic energy/, (m,v)=>m*v*v/2, 'Kinetic energy'],
[/^An object falls from rest through (\d+) m without losses\. Find its speed squared/, h=>20*h, 'v squared=2gh with stated g=10'],
[/^A machine receives (\d+) J and delivers (\d+) J/, (a,b)=>100*b/a, 'Efficiency'],
[/^A net force of (\d+) N acts on a (\d+) kg mass\./, (f,m)=>f/m, 'Newton second law'],
[/^A body rests on a horizontal surface with normal force (\d+) N\. If μk=([\d.]+)/, (n,u)=>n*u, 'Kinetic friction'],
[/^A spring with k=(\d+) N\/m is extended by ([\d.]+) m\./, (k,x)=>k*x, 'Hooke law magnitude'],
[/^A (\d+) kg body hangs motionless from a light vertical rope\./, m=>10*m, 'Equilibrium tension with stated g=10'],
[/^A (\d+) kg object's velocity changes from \+3 to −2 m\/s\./, m=>-5*m, 'Signed momentum change'],
[/^The separation of two point masses becomes (\d+) times as large\./, a=>1/(a*a), 'Inverse square gravity'],
[/^A (\d+) kg mass moves at (\d+) m\/s around a circle of radius (\d+) m\./, (m,v,r)=>m*v*v/r, 'Net centripetal force'],
[/^A perpendicular force of (\d+) N acts on ([\d.]+) m²\./, (f,a)=>f/a, 'Pressure'],
[/^A sample has mass (\d+) kg and volume ([\d.]+) m³\./, (m,v)=>m/v, 'Density'],
[/^A (\d+) kg object rests on a horizontal base of area ([\d.]+) m²\./, (m,a)=>m*10/a, 'Weight over area with stated g=10'],
[/^Find the gauge pressure at depth ([\d.]+) m in water of density (\d+) kg\/m³/, (h,rho)=>rho*10*h, 'Hydrostatic pressure with stated g=10'],
[/^A mercury column has height (\d+) cm\. Use ρHg=(\d+) kg\/m³ and g=(\d+) m\/s²\./, (h,rho,g)=>rho*g*h/100, 'Mercury head in SI units'],
[/^A liquid creates (\d+) Pa of pressure above the pressure outside a flat base of area ([\d.]+) m²\./, (p,a)=>p*a, 'Pressure difference times area'],
[/^A hydraulic press has output piston area (\d+) times the input area\. An input force of (\d+) N/, (r,f)=>r*f, 'Pascal ideal press'],
[/^An open water manometer has a (\d+) cm level difference\./, h=>100*h, 'rho g h, water and stated g=10'],
[/^An ideal barometer's fluid density doubles.*original height was (\d+) cm/, h=>h/2, 'Fixed pressure, doubled density'],
[/^Absolute pressure is (\d+) kPa and local atmospheric pressure is (\d+) kPa\./, (a,b)=>a-b, 'Gauge pressure'],
[/^An object displaces ([\d.]+) m³ of water\./, v=>10000*v, 'Buoyancy for stated rho=1000 and g=10'],
[/^An incompressible steady flow has inlet area (\d+) times its outlet area\. If inlet speed is (\d+) m\/s/, (r,v)=>r*v, 'Continuity'],
[/^An ideal gas is heated at fixed volume from (\d+) K to (\d+) K\./, (a,b)=>b/a, 'Pressure proportional to absolute temperature'],
[/^A gas absorbs (\d+) J of heat and does (\d+) J of work/, (q,w)=>q-w, 'First law, work done by gas'],
[/^A gas absorbs (\d+) J in an isochoric process\./, q=>q, 'Isochoric boundary work zero'],
[/^An ideal gas expands isothermally from volume (\d+) L to (\d+) L\. If its initial pressure is (\d+) kPa/, (v1,v2,p)=>p*v1/v2, 'Isothermal ideal gas'],
[/^A gas expands isobarically at (\d+) kPa by (\d+) litres\./, (p,v)=>p*v, 'Boundary work, kPa L equals J'],
[/^A gas receives (\d+) J of work during an adiabatic compression\./, w=>w, 'Adiabatic work on gas'],
[/^A heat engine absorbs (\d+) J and rejects (\d+) J per cycle\./, (h,c)=>100*(h-c)/h, 'Cyclic engine efficiency'],
[/^A refrigerator removes (\d+) J from its interior using (\d+) J of work\./, (q,w)=>q/w, 'Refrigerator COP'],
[/^A (\d+) kg material with specific heat (\d+) J\/\(kg·K\) warms by (\d+) K/, (m,c,t)=>m*c*t, 'Sensible heat'],
[/^A Coulomb force has magnitude (\d+) N\. If the separation increases by a factor of (\d+)/, (f,r)=>f/(r*r), 'Inverse square electrostatic force'],
[/^A positive test charge of (\d+) C feels a (\d+) N force\./, (q,f)=>f/q, 'Electric field definition'],
[/^A charge of (\d+) C passes a cross-section in (\d+) s\./, (q,t)=>q/t, 'Current definition'],
[/^A (\d+) Ω resistor carries (\d+) A\./, (r,i)=>r*i, 'Ohm law'],
[/^A ([\d.]+) m wire carries (\d+) A perpendicular to a (\d+) T field\./, (l,i,b)=>b*i*l, 'Magnetic wire force'],
[/^A wave has frequency (\d+) Hz and wavelength (\d+) m\./, (f,l)=>f*l, 'Wave speed'],
[/^A (\d+) g sample melts at constant temperature\. Its latent heat of fusion is (\d+) J\/g\./, (m,l)=>m*l, 'Phase transition heat'],
[/^In a closed system, (\d+) g of A reacts completely with (\d+) g of B to form only C\./, (a,b)=>a+b, 'Closed-system mass conservation'],
[/^A neutral atom has atomic number (\d+)\. How many protons/, z=>z, 'Atomic number definition'],
[/^Producers contain (\d+) kJ available energy\. If (\d+)% transfers/, (e,p)=>e*p/100, 'Explicit trophic transfer assumption'],
]
export function auditLegacy(original, rows, directory, addition) {
  const write=(name,value)=>fs.writeFileSync(path.join(directory,name),JSON.stringify(value,null,2)+'\n')
  const results=[]
  for(const row of rows) {
    const item=original[row.kind==='question'?'questions':'exercises'].find(q=>q.key===row.key)
    const text=item.text
    const rule=rules.find(([re])=>re.test(text))
    let scientificCheck={status:'not-adjudicated',method:'No deterministic calculation rule matched; scientific and distractor review remains open.'}
    if(rule && item.correctAnswer!=null) {
      const match=text.match(rule[0]); const expected=rule[1](...match.slice(1).map(num)); const actual=num(item.options[item.correctAnswer])
      if(Number.isFinite(actual)) {
        const ok=Math.abs(expected-actual)<=0.0000011
        scientificCheck={status:ok?'calculation-verified':'requires-correction',method:rule[2],expected,actual,scope:'Numerical key only. Distractor plausibility, wording, rights and optical interpretation are not certified.'}
        results.push({key:row.key,kind:row.kind,...scientificCheck})
      }
    }
    row.scientificCheck=scientificCheck
    row.usageRights={status:'unverified',evidence:'Bundled source attribution is not a licence. No rights-holder authorisation was supplied in this workspace.'}
    // Normalisation deliberately preserves case in scientific symbols/alleles.
    row.templateFamily=text.replace(/\d+(?:\.\d+)?/g,'#').replace(/\s+/g,' ').trim()
    if(/در سؤال ایزوتوپ جواب همیشه|ضعیف \+ بین مولکول/.test(item.solution||item.explanation||'')) {
      row.flags.push('overgeneralised-explanation'); row.status='requires-correction'
      row.correction='Isotope questions are not always answered by neutrons; hydrogen bonding can be intramolecular. Use the r2 original teaching/replacement, retaining this historical snapshot.'
    }
  }
  // Recomputing a numerical key never certifies the full item or admits it to mocks.
  assert.ok(results.length>100,'Insufficient deterministic legacy calculation coverage')
  const families=new Map()
  for(const r of rows.filter(r=>r.kind==='question')) { const a=families.get(r.templateFamily)||[];a.push(r.key);families.set(r.templateFamily,a) }
  const shared=[...families].filter(([,keys])=>keys.length>1).map(([template,keys])=>({template,keys,status:'related-template-family; not an assertion of semantic identity'}))
  const lessons=original.sections.map(s=>({key:s.key,sourceKey:s.sourceKey,topicKey:s.topicKey,language:/[\u0600-\u06ff]/.test(s.text)?'contains-Persian':'needs-language-review',scientificStatus:'not-fully-adjudicated',rightsStatus:'unverified',flags:[...(/همیشه|هرگز|always|never/i.test(s.text)?['absolute-claim-needs-context-review']:[]),...(s.html?.includes('<img')?['image-and-notation-optical-review']:[])],nextAction:'Use original versioned supplements where available; verify remaining prose, diagrams and source rights before new release approval.'}))
  const replacements=[
    {legacy:'biology/chapter1-chemical-foundation-fa.html#exercise-002',replacement:'chemistry/updates-2026-r1.html#periodic/question-isotope',issue:'Remove blanket tip that every isotope question is answered by neutrons; explicitly distinguish proton, neutron, mass number and ion electron counts.'},
    {legacy:'biology/chapter1-chemical-foundation-fa.html#exercise-004',replacement:'biology/updates-2026-r2.html#molecules-membranes/question-protein-hbond',issue:'Replace blanket intermolecular hydrogen-bond shortcut with a valid intramolecular protein example.'},
    {legacy:'biology/chapter1-chemical-foundation-fa.html#exercise-005',replacement:'chemistry/updates-2026-r2.html#buffers-hydrolysis',issue:'Explain pH through hydrogen-ion activity (dilute concentration approximation), not an undefined generic acidity ratio.'},
    {legacy:'logic/ch06-consistency.html#exercise-002',replacement:'logic/updates-2026-r2.html#models-evidence/question-necessary',issue:'Keep historically quarantined overlapping/changed-premise exercise ineligible. Use an original conditional-direction item instead.'}
  ]
  write('legacy-item-audit.json',rows); write('legacy-lesson-audit.json',lessons)
  write('legacy-calculations.json',results);write('template-families.json',shared);write('replacement-links.json',replacements)
  write('audit-summary.json',{legacyQuestions:original.questions.length,legacyExercises:original.exercises.length,legacyLessons:lessons.length,calculationChecks:results.length,calculationMismatches:results.filter(x=>x.status==='requires-correction').length,relatedTemplateFamilies:shared.length,originalReplacements:replacements.length,sourceRights:original.sources.map(s=>({key:s.key,status:'unverified',attribution:s.originalTitle,licence:'Not supplied; no licence invented'})),independentAcceptance:'pending; automated calculations and agent editorial review are not external academic, legal or psychometric certification',newQuestions:addition.questions.length,newPassages:2})
}
