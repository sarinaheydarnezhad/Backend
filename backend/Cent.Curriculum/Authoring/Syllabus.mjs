// Editorial paraphrases; S01..S31 are LOCAL audit keys, never CISIA identifiers.
export const sourceUrl = 'https://www.cisiaonline.it/en/cent/cent-s/structure-and-syllabus'
export const nodes = [
 ['S01','reading','Reading and comprehension',['B2 English comprehension','Explicit and implicit meaning','Formal and semantic relationships','Abstraction','Non-fiction, literary and journalistic texts']],
 ['S02','math','Numbers',['Number representations and order','Integer division and factors','Powers and roots','Percentages','Expression calculation']],
 ['S03','math','Algebra',['Expressions and factorisation','Solution sets','Linear and quadratic equations and inequalities','Systems']],
 ['S04','math','Geometry',['Plane and solid figures','Perimeter, area and volume','Similarity','Coordinates, distance, lines and circles']],
 ['S05','math','Functions',['Domain and range','Composition and inverse','Graphs and transformations','Elementary function families']],
 ['S06','math','Exponential and logarithms',['Logarithm definition and laws','Exponential/logarithmic equations and inequalities']],
 ['S07','math','Combinatorics and probability',['Finite counting','Permutations and combinations','Equally likely finite outcomes','Disjoint unions and independent intersections']],
 ['S08','math','Basic Statistics',['Data and measurement scales','Tables and charts','Absolute and relative frequency','Mean, median and mode']],
 ['S09','logic','Logic and deductive reasoning',['Connectives and quantifiers','Compatibility and equivalence','Sets and consequences','Negation and counterexamples','Necessary and sufficient conditions']],
 ['S10','logic','Interpretation and manipulation of data',['Move between representations','Extract and sort information','Evaluate data-supported claims','Choose a calculation process']],
 ['S11','logic','Problem solving and basic mathematical language',['Translate words into models','Ratios, percentages and means','Estimation','Validate a contextual answer']],
 ['S12','biology','Biological molecules',['Water and molecular interactions','Carbohydrates and lipids','Protein structure','Nucleic acids']],
 ['S13','biology','Cell biology',['Prokaryotes and eukaryotes','Membranes and organelles','Plant-specific structures']],
 ['S14','biology','Cell cycle, cell division, inheritance',['Genome organisation','Replication, transcription and translation','Cell cycle','Mitosis and meiosis','Mendelian inheritance']],
 ['S15','biology','Elements of plant biology and ecology',['Plant organs','Photosynthesis and energy flow','Food chains','Biotic interactions']],
 ['S16','biology','Elements of animal anatomy and physiology',['Tissue types','Musculoskeletal, digestive, respiratory, circulatory, excretory and nervous systems']],
 ['S17','chemistry','Macroscopic Properties of Matter',['Particle model and phases','Physical and chemical changes','Mixtures and separation','Conservation and combining laws']],
 ['S18','chemistry','Microscopic Properties of Matter and Composition of Substances',['Atomic structure and formulas','Lewis structures','Bond types and polarity','Intermolecular forces','VSEPR and hybridisation']],
 ['S19','chemistry','Periodic Trends and Atomic Structure',['Periods and groups','Quantum numbers and isotopes','Periodic trends','Electron configurations, Aufbau and Pauli']],
 ['S20','chemistry','Chemical Reactions and Stoichiometry',['Balancing','Moles and masses','Limiting reactant and yield','Concentration units']],
 ['S21','chemistry','Thermodynamics and Kinetics',['Gas laws and partial pressures','Internal energy, enthalpy, entropy and Gibbs energy','Equilibrium and reaction quotient','Activation energy and reaction rate']],
 ['S22','chemistry','Compounds and Solutions',['Inorganic names and formulas','Electrolytes and solubility','Conductivity and colligative effects','Metals']],
 ['S23','chemistry','Acids and Bases',['Acid-base theories','Strength, pH and indicators','Neutralisation','Salt hydrolysis and buffers']],
 ['S24','chemistry','Oxidation and Reduction (Redox)',['Oxidation numbers','Electron transfer and potentials','Agents and balancing']],
 ['S25','chemistry','Organic Chemistry',['Carbon structure and hybridisation','Combustion','Isomerism','Hydrocarbons, aromatics and oxygen-containing functional groups']],
 ['S26','chemistry','Applied Chemistry',['Measurements and uncertainty','Everyday transformations','Labels','Acid rain, greenhouse effect and smog','Chemical safety']],
 ['S27','physics','Physical quantities and measurement',['SI conversions and scientific notation','Functional models','Vector sum, difference, dot and cross products']],
 ['S28','physics','Point particle kinematics',['Position, velocity and acceleration','Linear motion and graphs','Free fall','Circular motion','Galilean frames']],
 ['S29','physics','Point particle dynamics, energy and work',['Inertia and forces','Newton second law','Work, power and kinetic energy','Gravitational potential and conservation']],
 ['S30','physics','Fluid mechanics and thermodynamics',['Density and pressure','Pascal, hydrostatics and buoyancy','Continuity and Bernoulli','Ideal gas and temperature scales','Heat, work and thermodynamic laws']],
 ['S31','physics','Electromagnetism principles',['Coulomb force and electric field','Conductors and insulators','Current, resistance and Ohm law','Magnets and field lines','Waves and electromagnetic spectrum']]
].map(([key,subject,title,competencies]) => ({key,subject,title,sourceUrl,locator:title,competencies}))

export const question = (id, text, options, correctAnswer, explanation, difficulty='medium') => ({id,text,options,correctAnswer,explanation,difficulty})
export const lesson = (subject,id,node,title,englishTitle,teaching,example,mistake,questions) => ({subject,id,node,title,englishTitle,teaching,example,mistake,questions})
