﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace ReplaceDungeonGenerator
{
    [RequireComponent(typeof(PatternView))]
    [RequireComponent(typeof(RuleSet))]
    public class ReplacementEngine : MonoBehaviour
    {
        class Match
        {
            public Vector3Int position;
            public Rule rule;

            public Match(Vector3Int position, Rule rule)
            {
                this.position = position;
                this.rule = rule;
            }
        }

        public enum ReplacementStrategy {
            First,
            Last,
            Random
        }

        public System.Random systemRandom = null;
        
        [SerializeField] private bool showDebugInformation = false;
        [SerializeField] private ReplacementStrategy replacementStrategy = ReplacementStrategy.Random;
        [SerializeField] [HideInInspector] private int currentStep;

        private List<Match> matches;
        private Dictionary<string, int> useCounts = new Dictionary<string, int>();

        public void ResetGeneration(int seed = -1)
        {
            if(seed != -1) {
                systemRandom = new System.Random(seed);
            }

            currentStep = 0;
            RuleSet ruleSet = GetComponent<RuleSet>();
            foreach(Rule r in ruleSet.rules) {
                useCounts[r.name] = 0;
            }
            Pattern mainPattern = GetComponent<PatternView>().pattern;
            Vector3Int mainPatternSize = mainPattern.Size;
            Vector3Int startPatternSize = ruleSet.startPattern.Size;
            Vector3Int position = new Vector3Int((mainPatternSize.x - startPatternSize.x) / 2, (mainPatternSize.y - startPatternSize.y) / 2, (mainPatternSize.z - startPatternSize.z) / 2);
            // Vector3Int position = Vector3Int.zero;
            SetPattern(position, ruleSet.startPattern);
            matches = null;
        }

        public bool SweepReplace(string filter = "", bool allowPartialMatch = true) {
            IEnumerable<Match> filteredMatches = GetMatches(filter, allowPartialMatch);
            if(filteredMatches == null) 
                return false;

            foreach(Match match in filteredMatches) {
                // replace
                Pattern replacement = match.rule.rightSide;
                Vector3Int position = match.position;
                SetPattern(position, replacement);
                useCounts[match.rule.name]++;
            }

            // assume enough was changed that it's worth recalculating all matches
            matches = null;

            currentStep++;
            return true;
        }

        public bool ReplaceMatch(string filter = "", bool allowPartialMatch = true)
        {
            Match match = FindMatch(replacementStrategy, filter, allowPartialMatch);
            
            if (match == null)
            {
                return false;
            }

            // replace
            Pattern replacement = match.rule.rightSide;
            Vector3Int position = match.position;
            SetPattern(position, replacement);
            useCounts[match.rule.name]++;

            // remove matches which are "dirty"
            for(int i = matches.Count-1; i >= 0; i--) {
                Match m = matches[i];

                if((m.rule.maximumApplications >= 0 && useCounts[m.rule.name] >= m.rule.maximumApplications)
                || Utils.BoundsIntersect(m.position, m.rule.leftSide.Size, position, replacement.Size)) {
                    matches.RemoveAt(i);
                }
            }

            // check for new matches in area
            matches.AddRange(
                FindMatches(
                    GetComponent<PatternView>().pattern, 
                    GetComponent<RuleSet>().rules, 
                    // position - Vector3Int.one, 
                    // replacement.Size + Vector3Int.one * 2
                    position, 
                    replacement.Size
                )
            );

            currentStep++;
            return true;
        }

        public void DoSubdividision(Vector3Int delta) {
            PatternView patternView = GetComponent<PatternView>();
            Pattern mainPattern = patternView.pattern;
            patternView.Subdivide(delta);

            // erases matches and will trigger new search
            matches = null;
        }

        private void SetPattern(Vector3Int position, Pattern pattern)
        {
            Vector3Int pSize = pattern.Size;
            Pattern mainPattern = GetComponent<PatternView>().pattern;

			foreach (Vector3Int p in Utils.IterateGrid3D(pSize))
			{
                Tile tile = pattern.tiles[p.x, p.y, p.z];
                if (tile.Label != Tile.Wildcard.Label) {
                    mainPattern.SetTile(position + p, tile, false);
                }
			}
            mainPattern.TriggerChangeEvents();
        }

        private Match FindMatch(ReplacementStrategy replacementStrategy, string filter = "", bool allowPartialMatch = true)
        {
            IEnumerable<Match> filteredMatches = GetMatches(filter, allowPartialMatch);
            if(filteredMatches == null) 
                return null;
 
            switch(replacementStrategy) {
                case ReplacementStrategy.First:
                    return filteredMatches.FirstOrDefault();
                case ReplacementStrategy.Last:
                    return filteredMatches.LastOrDefault();
                case ReplacementStrategy.Random:
                    return Utils.Choose(filteredMatches, WeightOfMatch, systemRandom);
                default:
                    Debug.Log("Replacement strategy not supported. ");
                    return null;
            }
        }

        private IEnumerable<Match> GetMatches(string filter, bool allowPartialMatch) {
            Pattern mainPattern = GetComponent<PatternView>().pattern;
            Vector3Int size = mainPattern.Size;
            List<Rule> rules = GetComponent<RuleSet>().rules;
            if(matches == null) {
                matches = FindAllMatches(mainPattern, rules);
            }

            if (matches.Count == 0)
            {
                // no match found
                return null;
            }

            IEnumerable<Match> filteredMatches;

            // pick a random match from results
            if(filter == "") {
                // unfiltered
                filteredMatches = matches;
            } else if(allowPartialMatch) {
                // filter start
                filteredMatches = matches.Where(m => MatchFilterFunction(filter, m));
            } else {
                // filter strict
                filteredMatches = matches.Where(m => filter == m.rule.name);
            }

            return filteredMatches;
        }

        private bool MatchFilterFunction(string filter, Match match) {
            return match.rule.name.StartsWith(filter);
        }

        private List<Match> FindAllMatches(Pattern mainPattern, List<Rule> rules) {
            return FindMatches(mainPattern, rules, Vector3Int.zero, mainPattern.Size);
        }

        private List<Match> FindMatches(Pattern mainPattern, List<Rule> rules, Vector3Int boundsPosition, Vector3Int boundsSize)
        {
            Vector3Int size = mainPattern.Size;
            List<Match> matchesFound = new List<Match>();

            // iterate over grid, rules, left side pattern of rule
            foreach (Rule ruleGroup in rules)
            {
                if (ruleGroup.maximumApplications >= 0 && useCounts[ruleGroup.name] >= ruleGroup.maximumApplications)
                {
                    continue;
                }
                if(ruleGroup.waitSteps > currentStep) 
                {
                    continue;
                }

                foreach (Rule r in ruleGroup.GetPermutations())
                {
                    Vector3Int searchPosition = boundsPosition - r.leftSide.Size + Vector3Int.one;
                    searchPosition = Utils.ClampVector3Int(searchPosition, size - Vector3Int.one);
                    Vector3Int searchSize = boundsSize + r.leftSide.Size - Vector3Int.one;
                    searchSize = Utils.ClampVector3Int(searchPosition + searchSize, size) - searchPosition;

                    foreach (Vector3Int relPos in Utils.IterateGrid3D(searchSize))
                    {
                        Vector3Int pos = searchPosition + relPos;
                        Pattern patternToMatch = r.leftSide;
                        Vector3Int pSize = patternToMatch.Size;
                        bool fail = false;

                        foreach (Vector3Int posLocal in Utils.IterateGrid3D(pSize))
                        {
                            Tile mainPatternTile = mainPattern.GetTile(pos + posLocal);
                            Tile patternToMatchTile = patternToMatch.tiles[posLocal.x, posLocal.y, posLocal.z];

                            // TODO make more elegant
                            // compare tiles of potential match and the main pattern
                            // we ignore wildcards
                            if (patternToMatchTile.Label != Tile.Wildcard.Label && patternToMatchTile.Label != mainPatternTile.Label)
                            {
                                fail = true;
                                break;
                            }
                        }

                        // if we matched, we remember the match for later
                        if (!fail)
                        {
                            matchesFound.Add(new Match(pos, r));
                        }
                    }
                }
            }

            return matchesFound;
        }

        /// Wrapper for rule weight, used by the choose utility function
        private float WeightOfMatch(Match m)
        {
            return m.rule.weight;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos() {
            if(showDebugInformation) {
                PatternView pv = GetComponent<PatternView>();
                if(matches != null) {
                    foreach(Match m in matches) {
                        Gizmos.color = Color.yellow;
                        Vector3 p1 = pv.GetPositionInWorldSpace(m.position, pv.displayDelta);
                        Vector3 p2 = pv.GetPositionInWorldSpace(m.position + m.rule.leftSide.Size - Vector3Int.one, pv.displayDelta);
                        Gizmos.DrawWireCube((p1 + p2) / 2, (p2 - p1) + pv.displayDelta);
                        Gizmos.color = Preferences.RoomLabelColor;
                        GUI.color = Preferences.RoomLabelColor;
                        Utils.DrawLabel(m.rule.name, p1);
                    }
                }
            }
        }
#endif

        private void OnValidate() {
            Debug.Assert(Utils.BoundsIntersect(
                new Vector3Int(0, 0, 0),
                new Vector3Int(1, 1, 1),
                new Vector3Int(0, 0, 0),
                new Vector3Int(1, 1, 1)
            ));
            Debug.Assert(Utils.BoundsIntersect(
                new Vector3Int(0, 0, 0),
                new Vector3Int(2, 2, 2),
                new Vector3Int(1, 0, 0),
                new Vector3Int(2, 2, 2)
            ));
            Debug.Assert(!Utils.BoundsIntersect(
                new Vector3Int(0, 0, 0),
                new Vector3Int(1, 2, 3),
                new Vector3Int(1, 0, 0),
                new Vector3Int(1, 2, 3)
            ));
        }
    }
}
