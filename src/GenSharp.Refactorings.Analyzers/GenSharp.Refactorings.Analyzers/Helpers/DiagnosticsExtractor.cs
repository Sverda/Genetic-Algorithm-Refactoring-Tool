﻿using System.Linq;
using GenSharp.Refactorings.Analyzers.Analyzers;
using GenSharp.Refactorings.Analyzers.Helpers.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace GenSharp.Refactorings.Analyzers.Helpers
{
    public class DiagnosticsExtractor
    {
        private readonly string _source;

        public DiagnosticsExtractor(string source)
        {
            _source = source;
        }

        public Diagnostic FromCode(int index)
        {
            var diagnostics = FromCode();
            var randomIndex = BetterRandom.Between(0, diagnostics.Length);
            return randomIndex >= diagnostics.Length ? diagnostics.FindExtractMethodDiagnostic() : diagnostics[index];
        }

        public Diagnostic[] FromCode()
        {
            var analyzers = GetCSharpDiagnosticAnalyzers();
            var diagnostics = analyzers
                .SelectMany(
                    analyzer => DiagnosticVerifier.GetSortedDiagnostics(new[] { _source }, analyzer), 
                    (analyzer, diagnostic) => new {analyzers, diagnostic});
            return diagnostics.Select(d => d.diagnostic).ToArray();
        }

        private static DiagnosticAnalyzer[] GetCSharpDiagnosticAnalyzers()
        {
            var analyzers = new DiagnosticAnalyzer[]
            {
                new ExtractStatementAnalyzer(),
                new ExtractMethodAnalyzer()
            };
            return analyzers;
        }
    }
}
