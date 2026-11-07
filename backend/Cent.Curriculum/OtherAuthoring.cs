using Cent.Module.Curriculum;

namespace Cent.Curriculum;

public static class OtherAuthoring
{
    public static void Build(Corpus c, Action<Corpus,string,string,string,string,string,string,string,string> add,
        Action<Corpus,string,string,double,string,double[]> number)
    {
        void Q(string topic, string text, string correct, string a, string b, string d, string why) => add(c,topic,text,correct,a,b,d,why,"medium");
        void N(string topic, string text, double correct, string why, params double[] wrong) => number(c,topic,text,correct,why,wrong);
        string Chemistry(int section) => $"chemistry/cent-chemistry-ch1-5-deep-1.html#section-{section:D2}";
        string Biology(int chapter, string keyword) => c.Topics.Single(t => t.Course == "biology" && t.ChapterKey.Contains("chapter"+chapter+"-") && t.Title.Contains(keyword)).Key;
        for (var v=2; v<=4; v++) {
            N(Chemistry(1),$"A {v*10} g sample melts at constant temperature. Its latent heat of fusion is 200 J/g. How much heat is absorbed, in joules?",v*2000,"At a phase-change plateau, Q=mL; temperature does not rise until the phase change finishes.");
            var changes=new[]{"iron rusting","wood burning","milk souring"};
            Q(Chemistry(2),$"Which change produces new chemical substances in example {v-1}?",changes[v-2],"Pure ice melting","Sugar dissolving in water","Cutting paper into pieces","Rusting, combustion and souring involve chemical changes; the other listed processes do not change chemical identity.");
            if(v==2) Q(Chemistry(3),"Why can a gas be compressed much more than a liquid?","There is much more empty space between gas particles","Gas particles have no mass","Gas particles disappear under pressure","Liquid particles cannot move at all","Most gas volume is empty space, whereas liquid particles are already close together.");
            if(v==3) Q(Chemistry(3),"A liquid flows but nearly keeps its volume. Which particle-level model explains this?","Particles remain close but can slide past one another","Particles occupy fixed lattice positions only","Particles are infinitely far apart","Particles have stopped moving","A liquid combines close particle spacing with mobility.");
            if(v==4) Q(Chemistry(3),"What directly produces the pressure of a gas on its container walls?","Collisions of gas particles with the walls","Only gravitational attraction","A complete absence of particle motion","The color of the container","Momentum transfer during repeated particle-wall collisions produces pressure.");
            var samples=new[]{"insoluble sand suspended in water","two miscible liquids with different boiling points","salt crystals required from salt water"};
            var methods=new[]{"Filtration","Distillation","Evaporation"};
            var wrong=methods.Where(m=>m!=methods[v-2]).Append("Magnetic separation").ToArray();
            Q(Chemistry(4),$"Choose the appropriate separation method for {samples[v-2]}.",methods[v-2],wrong[0],wrong[1],wrong[2],"Choose by physical properties: insoluble particle size, boiling-point difference, or removal of the solvent.");
            N(Chemistry(5),$"In a closed system, {10*v} g of A reacts completely with {5*v} g of B to form only C. Find the mass of C in grams.",15*v,"Conservation of mass in a closed system gives mass of products equal to total mass of reactants.");

            N(Biology(1,"ماده"),$"A neutral atom has atomic number {v+5}. How many protons does it have?",v+5,"The atomic number is the proton number. Neutrality additionally makes electron number equal to proton number.");
            if(v==2) Q(Biology(2,"غشای"),"An animal cell is placed in a hypotonic solution with impermeant solute. What is the initial net movement of water?","Into the cell by osmosis","Out of the cell by active transport","No net movement despite the concentration difference","Only through a sodium-potassium pump","Water enters the relatively more concentrated cell; an animal cell may swell or lyse.");
            if(v==3) Q(Biology(2,"غشای"),"An animal cell and its surrounding solution are isotonic. What is true of water movement?","Water moves both ways with no net flux","No water molecule crosses the membrane","All water leaves the cell","The cell must immediately lyse","At osmotic equilibrium the bidirectional fluxes balance, rather than molecular movement stopping.");
            if(v==4) Q(Biology(2,"غشای"),"A plant cell loses water in a hypertonic solution. Which change is expected?","The membrane can pull away from the cell wall","The cell wall dissolves instantly","The cell necessarily bursts from water entry","Water moves against its gradient without energy","Osmotic loss of water reduces turgor and may produce plasmolysis.");
            var crosses=new[]{"Aa × aa","AA × aa","Aa × Aa"}; var dominant=new[]{"1/2","1","3/4"};
            var alternatives=new[]{"0","1/4",v==3?"1/2":"1"}.Where(x=>x!=dominant[v-2]).ToList();
            foreach(var choice in new[]{"0","1/4","1/2","3/4","1"}) if(choice!=dominant[v-2]&&!alternatives.Contains(choice))alternatives.Add(choice);
            Q(Biology(3,"مندلی"),$"A is completely dominant over a. For the cross {crosses[v-2]}, what fraction of offspring are expected to show the dominant phenotype?",dominant[v-2],alternatives[0],alternatives[1],alternatives[2],"List gametes and combine them in a Punnett square. A dominant phenotype requires at least one A allele.");
            N(Biology(4,"جریان انرژی"),$"Producers contain {1000*v} kJ available energy. If 10% transfers to primary consumers, how much energy reaches those consumers in kJ?",100*v,"With an explicitly given 10% transfer efficiency, multiply the preceding trophic level's energy by 0.1.");
            Q(Biology(5,"درون"),$"After a meal, blood glucose rises from its fasting level by {v} units. Which hormone normally promotes glucose uptake and storage?","Insulin","Glucagon","ADH","Melatonin","Pancreatic beta cells secrete insulin in response to raised glucose; its effects help lower blood glucose.");
        }
        var review="logic/20-final-review.html#topic";
        Q(review,"Given P→Q and not Q, which conclusion is valid?","Not P","P","Q","P and Q","Modus tollens: if P held then Q would hold, contradicting not Q.");
        Q(review,"What is the negation of 'Every student solved at least one exercise'?","At least one student solved no exercises","Every student solved no exercises","Some students solved at least one exercise","No student exists","Negating ∀x∃y P(x,y) gives ∃x∀y not P(x,y).");
        Q(review,"Sales rose from 80 to 100 units. What was the percentage increase?","25%","20%","80%","125%","Percentage change uses the original value as denominator: (100−80)/80×100=25%.");
        Q("logic/ch06-consistency.html#topic","Premises: Every class member passed the exam. Tom is a class member. Tom did not pass the exam. Is this set consistent?","No; it entails both 'Tom passed' and 'Tom did not pass'","Yes, because Tom might be absent","Yes, because universal claims have no exceptions","It depends on Tom's grade","Instantiating the universal premise with Tom yields a direct contradiction.");
        Q("logic/ch13-quantifiers.html#topic","What is the negation of 'Every laboratory sample contains carbon'?","At least one laboratory sample does not contain carbon","No laboratory sample contains carbon","Every laboratory sample contains carbon","At least one sample contains carbon","The negation of a universal assertion is an existential counterexample, not a universal negative.");
    }
}
