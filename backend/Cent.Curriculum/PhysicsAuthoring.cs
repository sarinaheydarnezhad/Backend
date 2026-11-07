using Cent.Module.Curriculum;

namespace Cent.Curriculum;

public static class PhysicsAuthoring
{
    public static void Build(Corpus c, Action<Corpus,string,string,string,string,string,string,string,string> add,
        Action<Corpus,string,string,double,string,double[]> number, Func<int,int,string> key)
    {
        void Q(int ch, int s, string text, string correct, string a, string b, string d, string why) => add(c,key(ch,s),text,correct,a,b,d,why,"medium");
        void N(int ch, int s, string text, double correct, string why, params double[] wrong) => number(c,key(ch,s),text,correct,why,wrong);
        var counts = new[] { 9, 13, 14, 14, 10, 8 };
        for (var v = 2; v <= 4; v++) for (var ch = 1; ch <= 6; ch++) for (var s = 1; s <= counts[ch-1]; s++) {
            if (c.Questions.Count(q => q.TopicKey == key(ch,s)) >= 3) continue;
            switch ((ch,s)) {
                case (1,1): {
                    var names=new[]{"length","mass","time"}; var units=new[]{"metres","kilograms","seconds"};
                    Q(ch,s,$"A {names[v-2]} measurement is reported as '{v*3} {units[v-2]}'. What are the two essential parts of the measurement?", "A numerical value and a unit", "A number with no unit", "Only a measuring instrument", "Only a direction", "A physical quantity is expressed as a numerical magnitude multiplied by an agreed unit."); break;
                }
                case (1,2): N(ch,s,$"Convert {v*3} kilometres into metres.",v*3000,"One kilometre is 1000 metres."); break;
                case (1,3): {
                    var symbols=new[]{"speed v=s/t","force F=ma","energy E=mv²/2"}; var dims=new[]{"L T^-1","M L T^-2","M L² T^-2"};
                    Q(ch,s,$"What are the dimensions of {symbols[v-2]}?",dims[v-2],"L T","M T^-1","M L^-1", "Substitute dimensions of mass M, length L and time T into the defining relation."); break;
                }
                case (1,4): Q(ch,s,$"Which measurement requires a direction as well as its numerical value: scenario {v} with an object travelling along a straight road?","Velocity","Mass","Elapsed time","Kinetic energy","Velocity is a vector; mass, elapsed time and energy are scalars."); break;
                case (1,5): N(ch,s,$"Evaluate ({v}×10³)×(2×10^-2).",v*20,"Multiply coefficients and add powers of ten."); break;
                case (1,6): N(ch,s,$"A model has y=k/x². If x becomes {v} times as large, by what factor does y change?",1d/(v*v),"Inverse-square dependence gives a factor 1/n².",v*v,v,1d/v); break;
                case (1,7): N(ch,s,$"Perpendicular vectors have magnitudes {3*v} and {4*v}. Find the magnitude of their sum.",5*v,"Use the Pythagorean theorem for perpendicular components."); break;
                case (1,8): N(ch,s,$"A ruler's smallest division is 0.{v} cm. What is its resolution in millimetres?",v,"Multiply centimetres by 10 to obtain millimetres."); break;
                case (1,9): Q(ch,s,$"A balance always reads {v} g too high. Which change directly corrects this error?","Calibrate or zero the balance","Take more readings without calibration","Round every reading to an integer","Change grams into kilograms only","A persistent offset is systematic; repeating an uncalibrated measurement does not remove it."); break;
                case (2,1): N(ch,s,$"An object goes from x={v} m to x={v+7} m, then returns to x={v+2} m. Find the total displacement in metres.",2,"Displacement uses final position minus initial position, not total path length.",12,7,5); break;
                case (2,2): N(ch,s,$"A runner completes a {100*v} m trip in {10*v} s. Find average speed in m/s.",10,"Average speed is total distance divided by total time.",v,100,1); break;
                case (2,3): N(ch,s,$"Uniform motion starts at x₀=3 m with velocity {v} m/s. Find x after 5 s in metres.",3+5*v,"For uniform rectilinear motion, x=x₀+vt."); break;
                case (2,4): Q(ch,s,$"An object is at x=−{v} m and has velocity +3 m/s. Which statement is correct?","It is left of the origin and moving in the positive direction","It is right of the origin and moving in the negative direction","It must be moving away from the origin","Its speed is negative","The position sign gives location; the velocity sign gives direction. Speed is nonnegative."); break;
                case (2,5): Q(ch,s,$"The velocity is v(t)=(t−{v})². Does the object reverse direction at t={v}?","No: velocity is nonnegative on both sides","Yes: every zero velocity is a reversal","Yes: acceleration must be zero there","It moves in the negative direction after that time","A reversal requires a change in the sign of velocity, not merely a zero."); break;
                case (2,6): N(ch,s,$"Velocity increases from {v} to {v+12} m/s in 3 s. Find average acceleration in m/s².",4,"Average acceleration is Δv/Δt.",12,v,36); break;
                case (2,7): N(ch,s,$"A position–time graph is the line x={v}t+4 (SI). What is the velocity in m/s?",v,"The slope of a position–time graph is velocity.",4,0,v+4); break;
                case (2,8): N(ch,s,$"A velocity–time graph is horizontal at {v} m/s from t=0 to t=6 s. Find the displacement in metres.",6*v,"Displacement is the signed area under the velocity–time graph."); break;
                case (2,9): N(ch,s,$"Acceleration is constant at {v} m/s² for 4 s. What is the change in velocity in m/s?",4*v,"Change in velocity is the area under the acceleration–time graph."); break;
                case (2,10): N(ch,s,$"An object starts from rest and accelerates at {v} m/s² for 4 s. Find distance in metres.",8*v,"From rest, s=at²/2."); break;
                case (2,11): N(ch,s,$"A ball is dropped from rest for {v} s. Neglect air resistance and use g=10 m/s². Find its downward speed in m/s.",10*v,"Under constant gravitational acceleration, speed gt."); break;
                case (2,12): N(ch,s,$"Uniform circular motion has speed {2*v} m/s and radius {v} m. Find centripetal acceleration in m/s².",4*v,"Centripetal acceleration is v²/r."); break;
                case (2,13): N(ch,s,$"Two vehicles move in the same direction at {v+10} and {v+4} m/s. Find the faster vehicle's speed relative to the slower in m/s.",6,"Relative velocity in the same direction is the difference of velocities."); break;
                case (3,1): N(ch,s,$"A constant {v*10} N force acts parallel to a displacement of 3 m. Find its work in joules.",30*v,"W=Fd cosθ and θ=0."); break;
                case (3,2): N(ch,s,$"A {v} kg object is raised vertically by 3 m at constant speed. What is the work done by gravity in joules? Use g=10 m/s².",-30*v,"Gravity opposes upward displacement, so Wgravity=−mgh.",30*v,0,-10*v); break;
                case (3,3): N(ch,s,$"A {v} kg object initially at rest reaches 4 m/s. What is the net work in joules?",8*v,"Net work equals the change in kinetic energy, mv²/2."); break;
                case (3,4): N(ch,s,$"A machine performs {120*v} J of work in 6 s. Find its average power in watts.",20*v,"Average power is work divided by elapsed time."); break;
                case (3,5): N(ch,s,$"A {v} kg object moves at 6 m/s. What is its kinetic energy in joules?",18*v,"Kinetic energy is mv²/2."); break;
                case (3,6): N(ch,s,$"An object falls from rest through {5*v} m without losses. Find its speed squared in m²/s², using g=10 m/s².",100*v,"Conservation of energy gives v²=2gh."); break;
                case (3,7): N(ch,s,$"A machine receives {100*v} J and delivers {60*v} J of useful output. What is its efficiency as a percentage?",60,"Efficiency = useful output / input ×100%.",40,v,100); break;
                case (3,8): N(ch,s,$"A net force of {12*v} N acts on a {3*v} kg mass. Find acceleration in m/s².",4,"Newton's second law gives a=Fnet/m.",3,12,36); break;
                case (3,9): N(ch,s,$"A body rests on a horizontal surface with normal force {100*v} N. If μk=0.2, find kinetic friction in newtons when it slides.",20*v,"Kinetic friction magnitude is μkN."); break;
                case (3,10): N(ch,s,$"A spring with k={100*v} N/m is extended by 0.1 m. Find the restoring force magnitude in newtons.",10*v,"Hooke's law gives |F|=k|x|."); break;
                case (3,11): N(ch,s,$"A {v} kg body hangs motionless from a light vertical rope. Find tension in newtons, using g=10 m/s².",10*v,"Equilibrium requires tension to balance weight."); break;
                case (3,12): N(ch,s,$"A {v} kg object's velocity changes from +3 to −2 m/s. What is its change in momentum in kg·m/s?",-5*v,"Δp=m(vfinal−vinitial)=m(−2−3).",5*v,v,-v); break;
                case (3,13): N(ch,s,$"The separation of two point masses becomes {v} times as large. By what factor does their gravitational force change?",1d/(v*v),"Newtonian gravitation has an inverse-square dependence on separation.",v*v,v,1d/v); break;
                case (3,14): N(ch,s,$"A {v} kg mass moves at 4 m/s around a circle of radius 2 m. What net inward force is required in newtons?",8*v,"Fcentripetal=mv²/r."); break;
                case (4,1): {
                    var state=new[]{"solid","liquid","gas"}; var correct=new[]{"Fixed shape and fixed volume","Variable shape and nearly fixed volume","Variable shape and variable volume"};
                    Q(ch,s,$"Which description best matches an ordinary {state[v-2]} under normal conditions?",correct[v-2],"No particles and no mass","Always zero density","Shape and volume both independent of state","The particle model distinguishes rigidity, fluidity and compressibility."); break;
                }
                case (4,2): N(ch,s,$"A perpendicular force of {60*v} N acts on 0.2 m². Find pressure in pascals.",300*v,"Pressure is normal force divided by area."); break;
                case (4,3): N(ch,s,$"A sample has mass {v} kg and volume 0.002 m³. Find its density in kg/m³.",500*v,"Density is mass divided by volume."); break;
                case (4,4): N(ch,s,$"A {v} kg object rests on a horizontal base of area 0.02 m². Find its pressure on the base, using g=10 m/s².",500*v,"The force on the base is mg and pressure is mg/A."); break;
                case (4,5): N(ch,s,$"Find the gauge pressure at depth 0.{v} m in water of density 1000 kg/m³, using g=10 m/s².",1000*v,"Hydrostatic gauge pressure is ρgh."); break;
                case (4,6): N(ch,s,$"A mercury column has height {v} cm. Use ρHg=13600 kg/m³ and g=10 m/s². What pressure does it represent in pascals?",1360*v,"Convert centimetres to metres before applying ρgh."); break;
                case (4,7): N(ch,s,$"A liquid creates {1000*v} Pa of pressure above the pressure outside a flat base of area 0.02 m². Find the net pressure force in newtons.",20*v,"Pressure force due to a pressure difference is Δp A."); break;
                case (4,8): N(ch,s,$"A hydraulic press has output piston area {v} times the input area. An input force of 50 N gives what ideal output force in newtons?",50*v,"Equal fluid pressures imply Fout/Fin=Aout/Ain."); break;
                case (4,9): Q(ch,s,$"A measurement is taken {v*500} m higher in the atmosphere. All else being typical, atmospheric pressure:","Decreases","Increases in direct proportion to altitude","Is exactly unchanged","Becomes negative","There is less overlying air at higher altitude, so atmospheric pressure is lower."); break;
                case (4,10): N(ch,s,$"An open water manometer has a {v} cm level difference. Find the pressure difference in pascals with ρ=1000 kg/m³ and g=10 m/s².",100*v,"Use Δp=ρgΔh with height in metres."); break;
                case (4,11): N(ch,s,$"An ideal barometer's fluid density doubles while the atmospheric pressure stays fixed. If its original height was {v*20} cm, what is its new height in cm?",10*v,"Patm=ρgh, so at fixed pressure height is inversely proportional to density."); break;
                case (4,12): N(ch,s,$"Absolute pressure is {100+10*v} kPa and local atmospheric pressure is 100 kPa. Find gauge pressure in kPa.",10*v,"Gauge pressure = absolute pressure − atmospheric pressure."); break;
                case (4,13): N(ch,s,$"An object displaces {v*.001} m³ of water. Find the buoyant force in newtons, using ρ=1000 kg/m³ and g=10 m/s².",10*v,"Archimedes' principle gives Fbuoyant=ρfluid g Vdisplaced."); break;
                case (4,14): N(ch,s,$"An incompressible steady flow has inlet area {v} times its outlet area. If inlet speed is 2 m/s, find outlet speed in m/s.",2*v,"Continuity requires A1v1=A2v2."); break;
                case (5,1): N(ch,s,$"An ideal gas is heated at fixed volume from 300 K to {300*v} K. By what factor does its pressure change?",v,"At fixed n and V, the ideal-gas law gives P proportional to absolute temperature."); break;
                case (5,2): N(ch,s,$"A gas absorbs {100*v} J of heat and does 50 J of work on its surroundings. Find ΔU in joules.",100*v-50,"With Wby positive for work done by the gas, ΔU=Q−Wby."); break;
                case (5,3): N(ch,s,$"A gas absorbs {100*v} J in an isochoric process. Find ΔU in joules.",100*v,"Fixed volume gives W=0, so ΔU=Q."); break;
                case (5,4): N(ch,s,$"An ideal gas expands isothermally from volume 2 L to {2*v} L. If its initial pressure is 120 kPa, find its final pressure in kPa.",120d/v,"At constant temperature, P1V1=P2V2."); break;
                case (5,5): N(ch,s,$"A gas expands isobarically at 200 kPa by {v} litres. Find work done by the gas in joules.",200*v,"Wby=PΔV and 1 kPa·L = 1 J."); break;
                case (5,6): N(ch,s,$"A gas receives {100*v} J of work during an adiabatic compression. Find ΔU in joules.",100*v,"An adiabatic process has Q=0; work done on the gas increases its internal energy."); break;
                case (5,7): {
                    var process=new[]{"isochoric","isothermal (ideal gas)","adiabatic"}; var quantity=new[]{"boundary work","internal-energy change","heat transfer"};
                    Q(ch,s,$"In a {process[v-2]} process, which quantity must be zero?",quantity[v-2],"Absolute pressure","Absolute temperature","Amount of gas","Isochoric implies ΔV=0; ideal-gas isothermal implies ΔU=0; adiabatic implies Q=0."); break;
                }
                case (5,8): N(ch,s,$"A heat engine absorbs 1000 J and rejects {100*v} J per cycle. Find efficiency as a percentage.",100-10*v,"Efficiency=(Qhot−Qcold)/Qhot."); break;
                case (5,9): N(ch,s,$"A refrigerator removes {100*v} J from its interior using 100 J of work. Find its coefficient of performance.",v,"COPrefrigerator=Qcold/Win."); break;
                case (5,10): N(ch,s,$"A {v} kg material with specific heat 500 J/(kg·K) warms by 4 K without changing phase. Find heat absorbed in joules.",2000*v,"Use Q=mcΔT for a temperature change within one phase."); break;
                case (6,1): Q(ch,s,$"A neutral conductor loses {v} electrons. Its net charge is:","Positive","Negative","Still exactly zero","Magnetic but not electric","Losing negative charge leaves an excess of protons relative to electrons."); break;
                case (6,2): N(ch,s,$"A Coulomb force has magnitude 36 N. If the separation increases by a factor of {v}, find the new force in newtons.",36d/(v*v),"The electrostatic force is inversely proportional to distance squared."); break;
                case (6,3): N(ch,s,$"A positive test charge of 2 C feels a {10*v} N force. Find the electric field magnitude in N/C.",5*v,"The field is force per positive test charge: E=F/q."); break;
                case (6,4): N(ch,s,$"A charge of {12*v} C passes a cross-section in 4 s. Find current in amperes.",3*v,"Current is charge transferred per unit time."); break;
                case (6,5): N(ch,s,$"A {v} Ω resistor carries 3 A. Find the voltage across it in volts.",3*v,"Ohm's law is V=IR."); break;
                case (6,6): N(ch,s,$"A 0.5 m wire carries {v} A perpendicular to a 2 T field. Find the magnetic force in newtons.",v,"For a perpendicular wire, F=BIL."); break;
                case (6,7): N(ch,s,$"A wave has frequency {100*v} Hz and wavelength 2 m. Find its speed in m/s.",200*v,"Wave speed equals frequency times wavelength."); break;
                case (6,8): Q(ch,s,$"Compare photons with frequencies f and {v}f. The photon with the higher frequency has:",$"{v} times the energy","The same energy",$"1/{v} the energy","Zero energy","Photon energy is E=hf, proportional to frequency."); break;
                default: throw new InvalidOperationException("Missing physics topic template: " + key(ch,s));
            }
        }
    }
}
