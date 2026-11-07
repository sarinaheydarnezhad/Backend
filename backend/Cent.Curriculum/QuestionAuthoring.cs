using System.Globalization;
using Cent.Module.Curriculum;

namespace Cent.Curriculum;

public static class QuestionAuthoring
{
    public static void Build(Corpus corpus)
    {
        foreach (var exercise in corpus.Exercises.Where(e => e.Status == "source-mcq" && e.TopicKey is not null)) {
            var question = new BankQuestion(exercise.Key, exercise.Course, exercise.TopicKey!, exercise.SectionKey, exercise.Key,
                exercise.Text, exercise.Options, exercise.CorrectAnswer!.Value, exercise.Solution, exercise.Difficulty, exercise.FigureSvg, "source-mcq");
            if (exercise.Key == "logic/ch06-consistency.html#exercise-002") {
                corpus.Findings.Add(new(exercise.Key, "quarantined", "The explanation changes an unconditional universal statement into a conditional. The options also overlap; this item is excluded from scoring."));
                continue;
            }
            if (exercise.Key == "logic/ch14-barcharts.html#exercise-002") {
                question = question with { CorrectAnswer = 2, Explanation = "Farm A: 460−400=60 tons. Farm B: 210−150=60 tons. Both have the same absolute increase, so C is correct. The source's initial A label is a typo contradicted by its own calculation.", Origin = "review-corrected" };
                corpus.Findings.Add(new(exercise.Key, "corrected-key", "Corrected A to C: both absolute increases equal 60 tons."));
            }
            if (exercise.Key == "logic/ch08-equivalents.html#exercise-003") {
                question = question with { Options = ["Yes — the 'if' direction B → A of the biconditional gives this conclusion.", ..exercise.Options.Skip(1)],
                    Explanation = "A iff B includes A→B and B→A. Given B, use B→A. The source labels this the 'only if' direction in its option; that wording was corrected.", Origin = "review-corrected" };
                corpus.Findings.Add(new(exercise.Key, "clarified-option", "Corrected the direction named in option A: the conclusion uses B→A, not A→B."));
            }
            if (exercise.Key == "logic/ch11-square.html#exercise-002") {
                question = question with { Text = "At least one flight operated. " + question.Text, Origin = "review-clarified" };
                corpus.Findings.Add(new(exercise.Key, "explicit-assumption", "Added a non-empty domain so the traditional square-of-opposition answer is valid."));
            }
            var overrideTopic = exercise.Key switch {
                "physics/chapter1-physical-quantities.html#exercise-003" => "physics/chapter3-dynamics-work-energy.html#section-08",
                "physics/chapter1-physical-quantities.html#exercise-008" => "physics/chapter2-kinematics.html#section-02",
                "physics/chapter1-physical-quantities.html#exercise-014" => "physics/chapter2-kinematics.html#section-11",
                "physics/chapter2-kinematics.html#exercise-002" => "physics/chapter1-physical-quantities.html#section-02",
                "physics/chapter2-kinematics.html#exercise-003" => "physics/chapter2-kinematics.html#section-06",
                "physics/chapter5-thermodynamics.html#exercise-004" => "physics/chapter5-thermodynamics.html#section-03",
                "physics/chapter6-electromagnetism-waves.html#exercise-012" => "physics/chapter6-electromagnetism-waves.html#section-07",
                "logic/ch13-quantifiers.html#exercise-003" => "logic/ch07-negating-compounds.html#topic",
                _ => null
            };
            if (overrideTopic is not null) question = question with { TopicKey = overrideTopic };
            corpus.Questions.Add(question);
        }
        corpus.Findings.Add(new("logic/ch13-quantifiers.html", "canonical-title", "The HTML title says chapter 12. The visible chapter 13 heading is used for the catalog; original HTML is preserved unchanged."));
        Math(corpus);
        Physics(corpus);
        OtherCourses(corpus);
        foreach (var q in corpus.Questions) {
            if (!corpus.Topics.Any(t => t.Key == q.TopicKey && t.Course == q.Course) || !corpus.Sections.Any(s => s.Key == q.SectionKey))
                throw new InvalidOperationException("Question has an invalid source or topic: " + q.Key);
            if (q.Options.Length != 4 || q.Options.Distinct().Count() != 4 || q.CorrectAnswer is < 0 or > 3)
                throw new InvalidOperationException("Question options are not unique: " + q.Key);
        }
    }

    private static string M(string file, int section) => $"math/{file}.html#section-{section:D2}";
    private static string P(int chapter, int section) => $"physics/{PhysicsFiles[chapter - 1]}.html#section-{section:D2}";
    private static readonly string[] PhysicsFiles = ["chapter1-physical-quantities", "chapter2-kinematics", "chapter3-dynamics-work-energy", "chapter4-pressure", "chapter5-thermodynamics", "chapter6-electromagnetism-waves"];
    private static string F(double n) => n.ToString("0.######", CultureInfo.InvariantCulture);
    private static void Add(Corpus c, string topic, string prompt, string correct, string a, string b, string d, string explanation, string difficulty = "medium")
    {
        var definition = c.Topics.Single(t => t.Key == topic);
        var source = c.Sections.First(s => s.TopicKey == topic);
        var ordinal = c.Questions.Count(q => q.TopicKey == topic && q.Origin == "authored") + 1;
        var options = new[] { correct, a, b, d };
        var shift = ordinal % 4;
        options = options.Skip(shift).Concat(options.Take(shift)).ToArray();
        c.Questions.Add(new("authored:" + topic + ":" + ordinal, definition.Course, topic, source.Key, null, prompt, options,
            Array.IndexOf(options, correct), explanation, difficulty, null, "authored"));
    }
    private static void Number(Corpus c, string topic, string prompt, double answer, string explanation, params double[] wrong)
    {
        var alternatives = wrong.Concat(new[] { answer + 1, answer - 1, answer + 2, answer * 2 + 3 })
            .Select(F).Where(x => x != F(answer)).Distinct().Take(3).ToArray();
        Add(c, topic, prompt, F(answer), alternatives[0], alternatives[1], alternatives[2], explanation);
    }

    private static void Math(Corpus c)
    {
        for (var v = 2; v <= 4; v++) {
            var master = "mastercalss-riazi-fasl1-3";
            Add(c, M(master, 2), $"Which is the smallest listed number set containing √{v * v}?", "Natural numbers", "Irrational numbers", "Negative integers", "Non-real complex numbers", $"√{v * v}={v}; this is a positive integer and hence a natural number.");
            Add(c, M(master, 3), $"Write {v * 12000} in normalized scientific notation.", $"{F(v * 1.2)} × 10^4", $"{F(v * 1.2)} × 10^3", $"{F(v * 1.2)} × 10^5", $"{F(v * 12)} × 10^4", "Move the decimal point four places left; the coefficient must be at least 1 and below 10.");
            Number(c, M(master, 4), $"Which is the greatest number: −{v}, −{v + 1}, −{v + 3}, −{v + 5}?", -v, "Among negative numbers, the number closest to zero is greatest.", -v - 1, -v - 3, -v - 5);
            Number(c, M(master, 5), $"What is the greatest common divisor of {6 * v} and {9 * v}?", 3 * v, "The common prime factors give gcd(6n,9n)=3n.", v, 6 * v, 9 * v);
            Add(c, M(master, 6), $"Which listed number divides {6 * v + 9} exactly?", "3", "2", "5", "10", "The number is odd, does not end in 0 or 5, and its digit sum is divisible by 3.");
            Number(c, M(master, 7), $"A price of 200 increases by {10 * v}%. What is the new price?", 200 + 20 * v, "New price = original × (1 + percentage/100).", 200 + 10 * v, 200 - 20 * v, 20 * v);
            Number(c, M(master, 8), $"Evaluate 2^{v} × 2^3.", System.Math.Pow(2, v + 3), "When multiplying powers of the same base, add the exponents.", System.Math.Pow(2, v * 3), System.Math.Pow(2, v) + 8, v + 3);
            Number(c, M(master, 9), $"Evaluate √{9 * v * v}.", 3 * v, "The principal square root is nonnegative: √(9n²)=3n for positive n.", 9 * v, v, 3 * v * v);
            Number(c, M(master, 10), $"Evaluate {v} + 3 × 4^2.", v + 48, "Apply exponents first, then multiplication, then addition.", (v + 3) * 16, v + 12, (v + 12) * (v + 12));
            Add(c, "math.algebraic-expressions", $"Simplify {v + 2}x + 3 − {v}x + 5.", "2x + 8", $"{2 * v + 2}x + 8", "2x − 2", "10x", "Combine the x-terms separately from constants: (n+2−n)x+(3+5)=2x+8.");
            Add(c, "math.algebraic-expressions", $"Expand {v}(x − 3) + 2x.", $"{v + 2}x − {3 * v}", $"{v + 2}x − 3", $"{v}x − {3 * v}", $"{v + 2}x + {3 * v}", "Distribute to both terms inside parentheses, then combine like terms.");
            Number(c, "math.algebraic-expressions", $"If x={v}, evaluate 2x^2 − 3x + 1.", 2 * v * v - 3 * v + 1, "Substitute x with parentheses, apply the square, then multiply and add.", 2 * v - 3 * v + 1, 2 * v * v + 3 * v + 1, v * v - 3 * v + 1);
            Add(c, M(master, 12), $"Factor x^2 − {v * v}.", $"(x − {v})(x + {v})", $"(x − {v})^2", $"(x + {v})^2", $"x(x − {v * v})", "Use the difference-of-squares identity: a²−b²=(a−b)(a+b).");

            Number(c, M("chapter2", 2), $"Solve 3x + {v} = {3 * v + v}.", v, "Subtract the constant from both sides, then divide by 3.", v + 1, 3 * v, -v);
            Add(c, M("chapter2", 3), $"Solve −2x < {2 * v}.", $"x > −{v}", $"x < −{v}", $"x > {v}", $"x < {v}", "Dividing an inequality by a negative number reverses its direction.");
            Add(c, M("chapter2", 4), $"Find both roots of x² − {v + 1}x + {v} = 0.", $"1 and {v}", $"−1 and −{v}", $"0 and {v + 1}", $"−1 and {v}", $"The polynomial factors as (x−1)(x−{v}).");
            Add(c, M("chapter2", 5), $"Where is (x − {v})(x + {v}) negative?", $"−{v} < x < {v}", $"x < −{v} or x > {v}", $"x > {v}", "For every real x", "The factors have opposite signs between the two roots.");
            Add(c, M("chapter2", 6), $"Solve x² ≤ {v * v}.", $"−{v} ≤ x ≤ {v}", $"x ≤ −{v} or x ≥ {v}", $"x ≥ {v}", $"−{v} < x < {v}", "The upward-opening quadratic x²−n² is nonpositive between its roots, including the roots.");
            Number(c, M("chapter2", 7), $"Solve x+y={2 * v + 2} and x−y=2. What is x?", v + 2, "Add the equations: 2x=2n+4.", v, 2 * v + 2, 2);
            Number(c, M("chapter2", 8), $"What is the slope of the line through (0,1) and ({v},{2 * v + 1})?", 2, "Slope = change in y / change in x = 2n/n.", v, 1, 2 * v);
            Add(c, M("chapter2", 9), $"Two distinct lines have equations y={v}x+1 and y={v}x−3. They are:", "Parallel", "Perpendicular", "The same line", "Intersecting at the origin", "Equal slopes and different y-intercepts mean the lines are distinct and parallel.");
            Number(c, M("chapter2", 10), $"What is the distance between (0,0) and ({3 * v},{4 * v})?", 5 * v, "Use √(Δx²+Δy²), a scaled 3–4–5 right triangle.", 7 * v, v, 25 * v * v);
            Number(c, M("chapter2", 11), $"What is the distance from ({v},{v + 3}) to the line y={v}?", 3, "For a horizontal line, the perpendicular distance is the absolute difference of y-coordinates.", v, v + 3, 2 * v + 3);
            Add(c, M("chapter2", 12), $"Find the centroid of vertices (0,0), ({3 * v},0), (0,{3 * v}).", $"({v},{v})", $"({3 * v},{3 * v})", $"({F(1.5 * v)},{F(1.5 * v)})", "(0,0)", "Average the three x-coordinates and the three y-coordinates separately.");
            Number(c, M("chapter2", 13), $"What is the radius of (x−1)²+(y+2)²={v * v}?", v, "The right-hand side is r²; the radius is its positive square root.", v * v, v + 1, 2 * v);

            Number(c, M("chapter6-combinatorics-probability", 2), $"A code has one letter chosen from {v + 2} letters and one digit chosen from 10 digits. How many codes are possible?", (v + 2) * 10, "Independent successive choices multiply.", v + 12, v + 2, 10);
            Number(c, M("chapter6-combinatorics-probability", 3), $"In how many ways can {v + 1} distinct books be arranged in a row?", Factorial(v + 1), "The order of all distinct books matters, so use n!.", v + 1, (v + 1) * (v + 1), Factorial(v));
            Number(c, M("chapter6-combinatorics-probability", 4), $"How many unordered pairs can be selected from {v + 2} people?", (v + 2) * (v + 1) / 2, "Use C(n,2)=n(n−1)/2 because order does not matter.", (v + 2) * (v + 1), v + 2, v + 1);
            Add(c, M("chapter6-combinatorics-probability", 5), $"A fair die is rolled. What is the probability of a result at most {v}?", $"{v}/6", $"{v + 1}/6", $"1/{v}", "1", "There are n favorable equally likely outcomes out of six.");
            Add(c, M("chapter6-combinatorics-probability", 6), $"P(A)=0.{v}, P(B)=0.3 and A,B are mutually exclusive. Find P(A or B).", F(v / 10d + .3), F(v / 10d * .3), F(v / 10d), "1", "For disjoint events, P(A∪B)=P(A)+P(B).");
            Add(c, M("chapter6-combinatorics-probability", 7), $"Among {v + 4} red balls, {v} are marked. Given that a selected ball is red, what is the probability it is marked?", $"{v}/{v + 4}", $"{v + 4}/{v}", "1", $"1/{v + 4}", "Conditioning restricts the sample space to the red balls.");

            Add(c, M("chapter7-statistics", 2), $"A study selects {v * 100} residents from a city of 50,000. The selected residents form the:", "Sample", "Whole population", "Frequency", "Continuous variable", "A sample is the subset actually observed, while the population is the whole city.");
            Number(c, M("chapter7-statistics", 3), $"A value occurs {v} times in a dataset of 10 observations. What is its relative frequency?", v / 10d, "Divide the absolute frequency by the total number of observations.", v, 10d / v, 10 - v);
            Number(c, M("chapter7-statistics", 4), $"A pie-chart category represents {v * 10}% of observations. What is its central angle in degrees?", v * 36, "A full circle is 360°; multiply the proportion by 360.", v * 10, v * 3.6, v * 18);
            Number(c, M("chapter7-statistics", 5), $"Find the arithmetic mean of {v}, {v + 2}, {v + 4}.", v + 2, "Add the values and divide by three.", 3 * v + 6, v, v + 4);
            Number(c, M("chapter7-statistics", 6), $"Find the range of {v}, {v + 3}, {v + 8}.", 8, "Range = maximum − minimum.", v + 8, 3, v + 3);

            Number(c, M("geometry-chapter", 2), $"Two supplementary angles are {20 * v}° and x°. Find x.", 180 - 20 * v, "Supplementary angles sum to 180°.", 90 - 20 * v, 360 - 20 * v, 20 * v);
            Number(c, M("geometry-chapter", 3), $"A triangle has base {2 * v} and perpendicular height 5. Find its area.", 5 * v, "Triangle area is one half × base × height.", 10 * v, 2 * v + 5, v * v);
            Add(c, M("geometry-chapter", 4), $"What is the area of a circle of radius {v}?", $"{v * v}π", $"{v * v + 1}π", $"{v}π", $"{4 * v * v}π", "Circle area is πr², whereas circumference is 2πr.");
            Number(c, M("geometry-chapter", 5), $"A rectangular cuboid has dimensions {v}, 3 and 5. Find its volume.", 15 * v, "Multiply the three perpendicular side lengths.", v + 8, 2 * (v + 8), 8 * v);
            Number(c, M("geometry-chapter", 6), $"The side lengths of two similar figures have scale factor {v}. What is the ratio of their areas?", v * v, "Areas scale with the square of the linear scale factor.", v, v * v * v, 2 * v);

            Add(c, M("trig_chapter", 2), $"Convert {v * 30}° to radians.", $"{v}π/6", $"{v}π/3", $"{v}π/12", $"{v * 30}π", "Multiply degrees by π/180; equivalent unsimplified fractions are allowed in the correct option.");
            Add(c, M("trig_chapter", 3), $"A right triangle has opposite side {3 * v}, adjacent side {4 * v}, hypotenuse {5 * v}. Find sin θ.", "3/5", "4/5", "3/4", "5/3", "Sine is opposite divided by hypotenuse.");
            Number(c, M("trig_chapter", 4), $"Which quadrant contains an angle of {180 + 15 * v}°?", 3, "An angle strictly between 180° and 270° lies in quadrant III.", 1, 2, 4);
            Add(c, M("trig_chapter", 5), $"For θ={10 * v}°, simplify sin(180°−θ).", "sin θ", "−sin θ", "cos θ", "−cos θ", "Angles supplementary to θ have the same sine.");
            Number(c, M("trig_chapter", 6), $"If sin θ={v}/5 and cos θ is positive, what is sin(2θ) in terms of cos θ? Give the coefficient multiplying cos θ.", 2 * v / 5d, "Use sin(2θ)=2sinθcosθ.", v / 5d, -2 * v / 5d, v);
            Add(c, M("trig_chapter", 7), $"How many solutions does sin x=0 have in [0,{v}π]?", F(v + 1), F(v), F(2 * v), "0", "The solutions are integer multiples of π, including both endpoints.");
            Number(c, M("trig_chapter", 8), $"What is the maximum value of f(x)={v}sin x+1?", v + 1, "Since sin x≤1 and the amplitude is positive, the maximum is n+1.", v, 1 - v, 1);

            Add(c, M("فصل۳-توابع", 2), $"Find the real domain of f(x)=√(x−{v}).", $"x ≥ {v}", $"x ≤ {v}", "All real x", $"x ≠ {v}", "The radicand of a real square root must be nonnegative.");
            Add(c, M("فصل۳-توابع", 3), $"Which function is the inverse of f(x)={v}^x?", $"log_{v}(x)", $"x^{v}", $"{v}x", $"−{v}^x", "Logarithms undo exponentiation with the same positive base different from 1.");
            Number(c, M("فصل۳-توابع", 4), $"Find the x-coordinate of the vertex of y=x²−{2 * v}x+1.", v, "The vertex has x=−b/(2a).");
            Add(c, M("فصل۳-توابع", 5), $"Solve |x|={v}.", $"x={v} or x=−{v}", $"x={v} only", $"x=−{v} only", "No solution", "Absolute value is distance from zero, giving both symmetric values.");
            Add(c, M("فصل۳-توابع", 6), $"What value must be excluded from the domain of f(x)=1/(x−{v})?", F(v), F(-v), "0", "1", "A rational expression is undefined where its denominator is zero.");
            Number(c, M("فصل۳-توابع", 7), $"Solve 2^x={System.Math.Pow(2, v)}.", v, "Express both sides with base 2 and equate exponents.");
            Number(c, M("فصل۳-توابع", 8), $"Evaluate log_{v}({v * v}).", 2, "The logarithm asks which exponent gives the argument; n² is n raised to 2.", v, v * v, 1);
            Add(c, M("فصل۳-توابع", 9), $"Relative to y=x², y=(x−{v})² is shifted:", $"{v} units right", $"{v} units left", $"{v} units up", $"{v} units down", "Replacing x with x−a translates a graph a units right.");
            Number(c, M("فصل۳-توابع", 10), $"If f(x)=2x+1 and g(x)=x², find f(g({v})).", 2 * v * v + 1, "Evaluate the inner function g first, then apply f.", (2 * v + 1) * (2 * v + 1), 2 * v + 1, v * v + 1);
            Add(c, M("فصل۳-توابع", 11), $"The graph of y=x²−{v * v} lies below the x-axis for:", $"−{v}<x<{v}", $"x<−{v} or x>{v}", "Every real x", $"x={v} only", "Below the x-axis means y<0; the upward parabola is negative between its roots.");
        }
    }
    private static int Factorial(int n) => Enumerable.Range(1, n).Aggregate(1, (a, b) => a * b);

    private static void Physics(Corpus c) { PhysicsAuthoring.Build(c, Add, Number, P); }
    private static void OtherCourses(Corpus c) { OtherAuthoring.Build(c, Add, Number); }
}
