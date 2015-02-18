using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Configuration;
using System.Diagnostics;

namespace Orleans.TestFramework
{
    public class ParserStates
    {
        public const string Initialized = "Initialized";
        public const string Started = "Started";
        public const string Finished = "Finished";
        public const string Stable = "Stable";
    }
    class TransitionPattern
    {
        public string Name { get; set; }
        public string FromState { get; set; }
        public string ToState { get; set; }
        public string Pattern { get; set; }
        public int Count { get; set; }
        public int TotalCount { get; set; }
        public int Priority { get; set; }
        public bool Consecutive { get; set; }
    }
    class ParsingPattern
    {
        public string Name { get; set; }
        public string Current { get; set; }
        public string Pattern { get; set; }
        public bool AutoVariables { get; set; }
        public List<string> Variables = new List<string>();
    }
    public class ParserGrammar 
    {
        public string Name { get; set; }
        /// <summary>
        /// * means any state
        /// </summary>
        /// <param name="state1"></param>
        /// <param name="state2"></param>
        /// <returns></returns>
        static internal bool MatchesState(string state1, string state2)
        {
            if (state1 == "*") return true;
            if (state2 == "*") return true;
            return (state1 == state2);
        }
        static internal bool MatchesPattern(string pattern, string line)
        {
            Regex r = new Regex(pattern);
            Match m = r.Match(line);
            return m.Success;
        }
        static internal void ExtractValues(Dictionary<string, object> values, string pattern, string line, bool autoParse, List<string> variables)
        {
            Regex r = new Regex(pattern);
            Match m = r.Match(line);
            if (m.Success)
            {
                if (autoParse)
                {
                    ExtractValues(values, line);
                }
                else
                {
                    foreach (string variable in variables)
                    {
                        //string value = m.Captures[variable];
                        var value2 = m.Groups[variable];
                        if (!string.IsNullOrWhiteSpace(value2.Value)) values.Add(variable, value2.Value);
                    }
                }
            }
        }
        static internal void ExtractValues(Dictionary<string, object> values, string line)
        {
            //var index = line.LastIndexOf("00000");
            //line = line.Substring(index+5);
            string[] segments = line.Split(',');
            foreach (string segment in segments.Skip(1))
            {
                string[] parts = segment.Split(':');
                if(parts.Length > 1)
                {
                    string varName = parts[0].Trim();
                    string[] valueParts = parts[1].Trim().Split(' ');
                    if (valueParts.Length > 0)
                    {
                        var value = valueParts[0].Trim();
                        values.Add(varName, value);
                    }
                }
            }
        }
        internal List<TransitionPattern> Transitions = new List<TransitionPattern>();
        internal List<ParsingPattern> Lexers = new List<ParsingPattern>();

        public void AddTransitionPattern(string name, string fromState, string toState, string pattern, int count = 1, bool consecutive = false, int priority = 0)
        {
            TransitionPattern transition = new TransitionPattern();
            transition.Name = name;
            transition.FromState = fromState;
            transition.ToState = toState;
            transition.Pattern = pattern;
            transition.Priority = priority;
            transition.Count = count;
            transition.Consecutive = consecutive;
            Transitions.Add(transition);
        }
        public void AddParsingPattern(string name, string currentState, string pattern, bool autoVariables, params string[] variables)
        {
            ParsingPattern lexer = new ParsingPattern();
            lexer.Name = name;
            lexer.Current = currentState;
            lexer.Pattern = pattern;
            lexer.AutoVariables = autoVariables;
            lexer.Variables.AddRange(variables);
            Lexers.Add(lexer);
        }
    }
    public class QuickParser
    {
        public static bool DEBUG_ONLY_NO_WAITING = false;
        public static TimeSpan WAIT_BEFORE_KILLING_SILOS = TimeSpan.Zero;
        public static readonly TimeSpan WAIT_FOR_SILOS_TO_STABILIZE = TimeSpan.FromMinutes(2);
        public string CurrentState{get;private set;}
        public void ForceState(string state)
        {
            VisitedStates.Add(CurrentState); CurrentState = state;
            if (null != lastMatching) runningCounts[lastMatching] = 0;
        }
        public List<string> VisitedStates = new List<string>();
        public MetricCollector MetricCollector { get; set; }
        string fileName;
        public Dictionary<string,Action<QuickParser,string,string>> TransitionCallbacks = new Dictionary<string,Action<QuickParser,string, string>>();
        public string FileName
        {
            get { return fileName; }
        }
        IObservable<List<string>> fileObserver;
        ParserGrammar grammar;
        Dictionary<TransitionPattern, int> runningCounts = new Dictionary<TransitionPattern, int>();
        public QuickParser(ParserGrammar grammar)
        {
            this.grammar = grammar;
            isActive = true;
            foreach (TransitionPattern transition in this.grammar.Transitions)
            {
                runningCounts.Add(transition, 0);
            }
        }
        public void BeginAnalysis(string fileName, Func<bool> terminationCheck = null)
        {
            this.fileName = fileName;
            fileObserver = new FileObserver(fileName, 30*1000, 1, 1000, terminationCheck);
            BeginAnalysis();
        }
        public void BeginAnalysis(string fileName, IObservable<List<string>> source)
        {
            this.fileName = fileName;
            fileObserver = source;
            BeginAnalysis();
        }
        private void BeginAnalysis()
        {
            isActive = true;
            fileObserver.Subscribe(batch =>
                {
                    if (CurrentState == "Initialized") CurrentState = "Started";
                    foreach (string line in batch)
                    {
                        AnalyzeLine(line);
                    }
                },
                () => { VisitedStates.Add(CurrentState); CurrentState = "Finished"; });
            CurrentState = "Initialized";
        }
        private bool isActive;
        public void EndAnalysis()
        {
            isActive = false;
        }
        private TransitionPattern lastMatching;
        private string firstMatchingLine;
        public string IgnoredLineStartsWith = "*";

        private void AnalyzeLine(string line)
        {
            if (line.StartsWith(IgnoredLineStartsWith)) return;
            // First make any applicable transitions
            foreach(TransitionPattern applicable in 
                        from transition in grammar.Transitions where ParserGrammar.MatchesState(transition.FromState, CurrentState)
                        orderby transition.Priority
                        select transition)
            {
                if (ParserGrammar.MatchesPattern(applicable.Pattern, line))
                {
                    // if pattern is consecutive and it has matched at least once before AND last match is NOT same as this match 
                    // then reset the count.
                    // else increase the count
                    if ((applicable.Consecutive) && (runningCounts[applicable] > 0) && (applicable != lastMatching))
                    {
                        runningCounts[applicable] = 0;
                        firstMatchingLine = null;
                    }
                    else
                    {
                        if (runningCounts[applicable] == 0) firstMatchingLine = line;
                        runningCounts[applicable] = runningCounts[applicable] + 1;
                    }

                    if (runningCounts[applicable] == applicable.Count)
                    {
                        // make a transition
                        VisitedStates.Add(CurrentState);
                        Log.WriteLine(SEV.INFO, "QuickParser.State", "{0} -> {1}", CurrentState, applicable.ToState);
                        CurrentState = applicable.ToState;

                        if (TransitionCallbacks.ContainsKey(CurrentState))
                        {
                            try
                            {
                                TransitionCallbacks[CurrentState](this,firstMatchingLine,line);
                            }
                            catch
                            {
                            }
                        }
                        // reset the last count of previous match not that we are transitioning.
                        if (null != lastMatching) runningCounts[lastMatching] = 0;
                        // don't forget to save 
                        lastMatching = applicable;
                        break;
                    }
                    lastMatching = applicable;
                }
                else
                {
                    if (lastMatching!= null && lastMatching.Consecutive) runningCounts[lastMatching] = 0;
                    lastMatching = null;
                }
            }

            // Then try to extract variables
            Record values = ExtractValues(line);

            if (null != MetricCollector && null != values)
            {
                try
                {
                    MetricCollector.OnDataRecieved(fileName, values);
                }
                catch (Exception )
                { 
                }
            }
        }

        public Record ExtractValues(string line)
        {
            bool dataGathered = false;
            Record values = new Record();
            foreach (ParsingPattern applicable in
                        from lexer in grammar.Lexers
                        where ParserGrammar.MatchesState(lexer.Current, CurrentState)
                        select lexer)
            {
                ParserGrammar.ExtractValues(values, applicable.Pattern, line, applicable.AutoVariables, applicable.Variables);
                dataGathered = true;
            }
            return dataGathered ? (values.Count>0 ? values: null) : null;
        }

        public void WaitForState(string expected, int wait =10, int max =int.MaxValue)
        {
            if (DEBUG_ONLY_NO_WAITING) return;
            if (null != expected)
            {
                int i = 0;
                while (true) 
                {
                    if (!isActive) return;
                    if(ParserGrammar.MatchesState(CurrentState, "Finished")) return;
                    if (ParserGrammar.MatchesState(CurrentState, expected)) return;
                    var visitedStates = VisitedStates.ToArray();
                    foreach (string visited in visitedStates)
                    {
                        if (ParserGrammar.MatchesState(visited, expected)) return;
                    }
                    if(i > max) break;
                    i++;
                    Thread.Sleep(wait);
                }
                if (!(i < max))
                {
                    throw new TimeoutException("WaitForState: Timeout while waiting for state " + expected);
                }
            }
        }

        public static void WaitForStateAll(List<QuickParser> parsers, string expected, bool checkVisited = true, int wait = 100, int max = int.MaxValue)
        {
            if (DEBUG_ONLY_NO_WAITING) return;
            if (null != expected)
            {
                int i = 0;
                
                while(true)
                {
                    int doneCount = 0;
                    bool done = true;
                    if (i > max) break;
                    foreach (QuickParser parser in parsers)
                    {
                        if (!parser.isActive) continue;
                        if (parser.CurrentState == "Exception")
                        {
                            throw new Exception(
                                string.Format("Parser encounter too many/critical errors. Current State:{0} Expected State:{1} File:{2}", 
                                parser.CurrentState,
                                expected,
                                parser.fileName));
                        }
                        // check visited states before finished state.
                        bool matches = ParserGrammar.MatchesState(parser.CurrentState, expected);
                        // check if we have already visted this state in past.
                        if (!matches && checkVisited)
                        {
                            foreach (string visited in parser.VisitedStates)
                            {
                                if (ParserGrammar.MatchesState(visited, expected))
                                {
                                    matches = true;
                                    break;
                                }
                            }
                        }
                        if ((parser.CurrentState == "Finished") && !(expected == "Finished") && !matches)
                        {
                            throw new Exception(
                            string.Format("Parser finished unexpectedly. Current State:{0} Expected State:{1} File:{2}",
                            parser.CurrentState,
                            expected,
                            parser.fileName));
                        }
                        if(matches)doneCount++;
                        done = (done && matches);
                    }
                    if (done) break;
                    i++;
                    Thread.Sleep(wait);
                } 
                if (!(i < max))
                {
                    throw new TimeoutException("WaitForStateAll: Timeout while waiting for state " + expected);
                }
            }
        }
        public static void WaitForStateAny(List<QuickParser> parsers, string expected, int wait = 100, int max = int.MaxValue)
        {
            if (DEBUG_ONLY_NO_WAITING) return;

            if (expected != null)
            {
                int i = 0;
                while (true)
                {
                    if (i > max) 
                        break;
                    foreach (QuickParser parser in parsers)
                    {
                        if (!parser.isActive) 
                            return;
                        if (parser.CurrentState == "Exception")
                        {
                            throw new Exception(
                                string.Format("Parser encounter too many/critical errors. Current State:{0} Expected State:{1} File:{2}",
                                parser.CurrentState,
                                expected,
                                parser.fileName));
                        }
                        // check current and visited states first.
                        if (ParserGrammar.MatchesState(parser.CurrentState, expected)) 
                            return;
                        foreach (string visited in parser.VisitedStates)
                        {
                            if (ParserGrammar.MatchesState(visited, expected))
                            {
                                return;
                            }
                        }
                        if ((parser.CurrentState == "Finished") && !(expected == "Finished"))
                        {
                            throw new Exception(
                                string.Format("Parser finished unexpectedly. Current State:{0} Expected State:{1} File:{2}",
                                    parser.CurrentState,
                                    expected,
                                    parser.fileName));
                        }
                    }
                    i++;
                    Thread.Sleep(wait);
                }
                if (!(i < max))
                {
                    throw new TimeoutException("WaitForStateAny: Timeout while waiting for state " + expected);
                }
            }
        }
    }
}
