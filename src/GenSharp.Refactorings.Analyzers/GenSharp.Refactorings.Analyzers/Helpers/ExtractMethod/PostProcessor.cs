﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace GenSharp.Refactorings.Analyzers.Helpers.ExtractMethod
{
    internal class PostProcessor
    {
        private readonly SemanticModel _semanticModel;
        private readonly int _contextPosition;

        public PostProcessor(SemanticModel semanticModel, int contextPosition = 0)
        {
            _semanticModel = semanticModel;
            _contextPosition = contextPosition;
        }

        public IEnumerable<StatementSyntax> MergeDeclarationStatements(IEnumerable<StatementSyntax> statements)
        {
            if (statements.FirstOrDefault() == null)
            {
                return statements;
            }

            return MergeDeclarationStatementsWorker(statements);
        }

        private IEnumerable<StatementSyntax> MergeDeclarationStatementsWorker(IEnumerable<StatementSyntax> statements)
        {
            var map = new Dictionary<ITypeSymbol, List<LocalDeclarationStatementSyntax>>();
            foreach (var statement in statements)
            {
                if (!IsDeclarationMergable(statement))
                {
                    foreach (var declStatement in GetMergedDeclarationStatements(map))
                    {
                        yield return declStatement;
                    }

                    yield return statement;
                    continue;
                }

                AppendDeclarationStatementToMap(statement as LocalDeclarationStatementSyntax, map);
            }

            // merge leftover
            if (map.Count <= 0)
            {
                yield break;
            }

            foreach (var declStatement in GetMergedDeclarationStatements(map))
            {
                yield return declStatement;
            }
        }

        private IEnumerable<LocalDeclarationStatementSyntax> GetMergedDeclarationStatements(
            Dictionary<ITypeSymbol, List<LocalDeclarationStatementSyntax>> map)
        {
            foreach (var keyValuePair in map)
            {
                // merge all variable decl for current type
                var variables = new List<VariableDeclaratorSyntax>();
                foreach (var statement in keyValuePair.Value)
                {
                    foreach (var variable in statement.Declaration.Variables)
                    {
                        variables.Add(variable);
                    }
                }

                // and create one decl statement
                // use type name from the first decl statement
                yield return
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(keyValuePair.Value.First().Declaration.Type, SyntaxFactory.SeparatedList(variables)));
            }

            map.Clear();
        }

        private void AppendDeclarationStatementToMap(
            LocalDeclarationStatementSyntax statement,
            Dictionary<ITypeSymbol, List<LocalDeclarationStatementSyntax>> map)
        {
            var type = ModelExtensions.GetSpeculativeTypeInfo(_semanticModel, _contextPosition, statement.Declaration.Type, SpeculativeBindingOption.BindAsTypeOrNamespace).Type;

            map.GetOrAdd(type, _ => new List<LocalDeclarationStatementSyntax>()).Add(statement);
        }

        private bool IsDeclarationMergable(StatementSyntax statement)
        {
            // to be mergable, statement must be
            // 1. decl statement without any extra info
            // 2. no initialization on any of its decls
            // 3. no trivia except whitespace
            // 4. type must be known

            if (!(statement is LocalDeclarationStatementSyntax declarationStatement))
            {
                return false;
            }

            if (declarationStatement.Modifiers.Count > 0 ||
                declarationStatement.IsConst ||
                declarationStatement.IsMissing)
            {
                return false;
            }

            if (ContainsAnyInitialization(declarationStatement))
            {
                return false;
            }

            if (!ContainsOnlyWhitespaceTrivia(declarationStatement))
            {
                return false;
            }

            var semanticInfo = ModelExtensions.GetSpeculativeTypeInfo(_semanticModel, _contextPosition, declarationStatement.Declaration.Type, SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
            if (semanticInfo == null ||
                semanticInfo.TypeKind == TypeKind.Error ||
                semanticInfo.TypeKind == TypeKind.Unknown)
            {
                return false;
            }

            return true;
        }

        private bool ContainsAnyInitialization(LocalDeclarationStatementSyntax statement)
        {
            foreach (var variable in statement.Declaration.Variables)
            {
                if (variable.Initializer != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsOnlyWhitespaceTrivia(StatementSyntax statement)
        {
            foreach (var token in statement.DescendantTokens())
            {
                foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
                {
                    if (trivia.Kind() != SyntaxKind.WhitespaceTrivia &&
                        trivia.Kind() != SyntaxKind.EndOfLineTrivia)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public IEnumerable<StatementSyntax> RemoveRedundantBlock(IEnumerable<StatementSyntax> statements)
        {
            // it must have only one statement
            if (statements.Count() != 1)
            {
                return statements;
            }

            // that statement must be a block
            if (!(statements.Single() is BlockSyntax block))
            {
                return statements;
            }

            // we have a block, remove them
            return RemoveRedundantBlock(block);
        }

        private IEnumerable<StatementSyntax> RemoveRedundantBlock(BlockSyntax block)
        {
            // if block doesn't have any statement
            if (block.Statements.Count == 0)
            {
                return new List<StatementSyntax>();
            }

            // okay transfer asset attached to block to statements
            var firstStatement = block.Statements.First();
            var firstToken = firstStatement.GetFirstToken(includeZeroWidth: true);
            var firstTokenWithAsset = block.OpenBraceToken.CopyAnnotationsTo(firstToken).WithPrependedLeadingTrivia(block.OpenBraceToken.GetAllTrivia());

            var lastStatement = block.Statements.Last();
            var lastToken = lastStatement.GetLastToken(includeZeroWidth: true);
            var lastTokenWithAsset = block.CloseBraceToken.CopyAnnotationsTo(lastToken).WithAppendedTrailingTrivia(block.CloseBraceToken.GetAllTrivia());

            // create new block with new tokens
            block = block.ReplaceTokens(new[] { firstToken, lastToken }, (o, c) => (o == firstToken) ? firstTokenWithAsset : lastTokenWithAsset);

            // return only statements without the wrapping block
            return block.Statements;
        }

        public IEnumerable<StatementSyntax> RemoveDeclarationAssignmentPattern(IEnumerable<StatementSyntax> statements)
        {
            if (!(statements.ElementAtOrDefault(0) is LocalDeclarationStatementSyntax declaration) || !(statements.ElementAtOrDefault(1) is ExpressionStatementSyntax assignment))
            {
                return statements;
            }

            if (ContainsAnyInitialization(declaration) ||
                declaration.Declaration is null ||
                declaration.Declaration.Variables.Count != 1 ||
                assignment.Expression is null ||
                assignment.Expression.Kind() != SyntaxKind.SimpleAssignmentExpression)
            {
                return statements;
            }

            if (!ContainsOnlyWhitespaceTrivia(declaration) ||
                !ContainsOnlyWhitespaceTrivia(assignment))
            {
                return statements;
            }

            var variableName = declaration.Declaration.Variables[0].Identifier.ToString();

            var assignmentExpression = assignment.Expression as AssignmentExpressionSyntax;
            if (assignmentExpression.Left is null ||
                assignmentExpression.Right is null ||
                assignmentExpression.Left.ToString() != variableName)
            {
                return statements;
            }

            var variable = declaration.Declaration.Variables[0].WithInitializer(SyntaxFactory.EqualsValueClause(assignmentExpression.Right));
            var localDeclarationStatementSyntax = declaration.WithDeclaration(
                declaration.Declaration.WithVariables(
                    SyntaxFactory.SingletonSeparatedList(variable)));
            return new List<StatementSyntax>(new []{ localDeclarationStatementSyntax }).Concat(statements.Skip(2));
        }

        public IEnumerable<StatementSyntax> RemoveInitializedDeclarationAndReturnPattern(IEnumerable<StatementSyntax> statements)
        {
            // if we have inline temp variable as service, we could just use that service here.
            // since it is not a service right now, do very simple clean up
            if (statements.ElementAtOrDefault(2) != null)
            {
                return statements;
            }

            if (!(statements.ElementAtOrDefault(0) is LocalDeclarationStatementSyntax declaration) || !(statements.ElementAtOrDefault(1) is ReturnStatementSyntax returnStatement))
            {
                return statements;
            }

            if (declaration.Declaration == null ||
                declaration.Declaration.Variables.Count != 1 ||
                declaration.Declaration.Variables[0].Initializer == null ||
                declaration.Declaration.Variables[0].Initializer.Value == null ||
                declaration.Declaration.Variables[0].Initializer.Value is StackAllocArrayCreationExpressionSyntax ||
                returnStatement.Expression == null)
            {
                return statements;
            }

            if (!ContainsOnlyWhitespaceTrivia(declaration) ||
                !ContainsOnlyWhitespaceTrivia(returnStatement))
            {
                return statements;
            }

            var variableName = declaration.Declaration.Variables[0].Identifier.ToString();
            if (returnStatement.Expression.ToString() != variableName)
            {
                return statements;
            }

            var returnStatementSyntax = SyntaxFactory.ReturnStatement(declaration.Declaration.Variables[0].Initializer.Value);
            return new[] {returnStatementSyntax};
        }
    }
}
