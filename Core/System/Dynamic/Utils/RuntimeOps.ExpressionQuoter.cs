﻿#if NET20 || NET30

// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using Theraot.Core.System.Linq.Expressions;
using Theraot.Core.System.Linq.Expressions.Compiler;
using Theraot.Core.System.Runtime.CompilerServices;

namespace Theraot.Core.System.Dynamic.Utils
{
    public partial class RuntimeOps
    {
        /// <summary>
        /// Quotes the provided expression tree.
        /// </summary>
        /// <param name="expression">The expression to quote.</param>
        /// <param name="hoistedLocals">The hoisted local state provided by the compiler.</param>
        /// <param name="locals">The actual hoisted local values.</param>
        /// <returns>The quoted expression.</returns>
        [Obsolete("do not use this method", true), EditorBrowsable(EditorBrowsableState.Never)]
        public static Expression Quote(Expression expression, object hoistedLocals, object[] locals)
        {
            Debug.Assert(hoistedLocals != null && locals != null);
            var quoter = new ExpressionQuoter((HoistedLocals)hoistedLocals, locals);
            return quoter.Visit(expression);
        }

        /// <summary>
        /// Combines two runtime variable lists and returns a new list.
        /// </summary>
        /// <param name="first">The first list.</param>
        /// <param name="second">The second list.</param>
        /// <param name="indexes">The index array indicating which list to get variables from.</param>
        /// <returns>The merged runtime variables.</returns>
        [Obsolete("do not use this method", true), EditorBrowsable(EditorBrowsableState.Never)]
        public static IRuntimeVariables MergeRuntimeVariables(IRuntimeVariables first, IRuntimeVariables second, int[] indexes)
        {
            return new MergedRuntimeVariables(first, second, indexes);
        }

        // Modifies a quoted Expression instance by changing hoisted variables and
        // parameters into hoisted local references. The variable's StrongBox is
        // burned as a constant, and all hoisted variables/parameters are rewritten
        // as indexing expressions.
        //
        // The behavior of Quote is indended to be like C# and VB expression quoting
        private sealed class ExpressionQuoter : ExpressionVisitor
        {
            private readonly HoistedLocals _scope;
            private readonly object[] _locals;

            // A stack of variables that are defined in nested scopes. We search
            // this first when resolving a variable in case a nested scope shadows
            // one of our variable instances.
            private readonly Stack<Set<ParameterExpression>> _shadowedVars = new Stack<Set<ParameterExpression>>();

            internal ExpressionQuoter(HoistedLocals scope, object[] locals)
            {
                _scope = scope;
                _locals = locals;
            }

            protected internal override Expression VisitLambda<T>(Expression<T> node)
            {
                _shadowedVars.Push(new Set<ParameterExpression>(node.Parameters));
                var b = Visit(node.Body);
                _shadowedVars.Pop();
                if (b == node.Body)
                {
                    return node;
                }
                return Expression.Lambda<T>(b, node.Name, node.TailCall, node.Parameters);
            }

            protected internal override Expression VisitBlock(BlockExpression node)
            {
                if (node.Variables.Count > 0)
                {
                    _shadowedVars.Push(new Set<ParameterExpression>(node.Variables));
                }
                var b = Visit(node.Expressions);
                if (node.Variables.Count > 0)
                {
                    _shadowedVars.Pop();
                }
                if (b == node.Expressions)
                {
                    return node;
                }
                return Expression.Block(node.Variables, b);
            }

            protected override CatchBlock VisitCatchBlock(CatchBlock node)
            {
                if (node.Variable != null)
                {
                    _shadowedVars.Push(new Set<ParameterExpression>(new[] { node.Variable }));
                }
                var b = Visit(node.Body);
                var f = Visit(node.Filter);
                if (node.Variable != null)
                {
                    _shadowedVars.Pop();
                }
                if (b == node.Body && f == node.Filter)
                {
                    return node;
                }
                return Expression.MakeCatchBlock(node.Test, node.Variable, b, f);
            }

            protected internal override Expression VisitRuntimeVariables(RuntimeVariablesExpression node)
            {
                var count = node.Variables.Count;
                var boxes = new List<IStrongBox>();
                var vars = new List<ParameterExpression>();
                var indexes = new int[count];
                for (var i = 0; i < count; i++)
                {
                    var box = GetBox(node.Variables[i]);
                    if (box == null)
                    {
                        indexes[i] = vars.Count;
                        vars.Add(node.Variables[i]);
                    }
                    else
                    {
                        indexes[i] = -1 - boxes.Count;
                        boxes.Add(box);
                    }
                }

                // No variables were rewritten. Just return the original node
                if (boxes.Count == 0)
                {
                    return node;
                }

                var boxesConst = Expression.Constant(new RuntimeVariables(boxes.ToArray()), typeof(IRuntimeVariables));
                // All of them were rewritten. Just return the array as a constant
                if (vars.Count == 0)
                {
                    return boxesConst;
                }

                // Otherwise, we need to return an object that merges them
                return Expression.Call(
                    typeof(RuntimeOps).GetMethod("MergeRuntimeVariables"),
                    Expression.RuntimeVariables(new TrueReadOnlyCollection<ParameterExpression>(vars.ToArray())),
                    boxesConst,
                    Expression.Constant(indexes)
                );
            }

            protected internal override Expression VisitParameter(ParameterExpression node)
            {
                var box = GetBox(node);
                if (box == null)
                {
                    return node;
                }
                return Expression.Field(Expression.Constant(box), "Value");
            }

            private IStrongBox GetBox(ParameterExpression variable)
            {
                // Skip variables that are shadowed by a nested scope/lambda
                foreach (var hidden in _shadowedVars)
                {
                    if (hidden.Contains(variable))
                    {
                        return null;
                    }
                }

                var scope = _scope;
                var locals = _locals;
                while (true)
                {
                    int hoistIndex;
                    if (scope.Indexes.TryGetValue(variable, out hoistIndex))
                    {
                        return (IStrongBox)locals[hoistIndex];
                    }
                    scope = scope.Parent;
                    if (scope == null)
                    {
                        break;
                    }
                    locals = HoistedLocals.GetParent(locals);
                }

                // Unbound variable: an error should've been thrown already
                // from VariableBinder
                throw ContractUtils.Unreachable;
            }
        }

        private sealed class RuntimeVariables : IRuntimeVariables
        {
            private readonly IStrongBox[] _boxes;

            internal RuntimeVariables(IStrongBox[] boxes)
            {
                _boxes = boxes;
            }

            int IRuntimeVariables.Count
            {
                get { return _boxes.Length; }
            }

            object IRuntimeVariables.this[int index]
            {
                get { return _boxes[index].Value; }

                set { _boxes[index].Value = value; }
            }
        }

        /// <summary>
        /// Provides a list of variables, supporing read/write of the values
        /// Exposed via RuntimeVariablesExpression
        /// </summary>
        private sealed class MergedRuntimeVariables : IRuntimeVariables
        {
            private readonly IRuntimeVariables _first;
            private readonly IRuntimeVariables _second;

            // For reach item, the index into the first or second list
            // Positive values mean the first array, negative means the second
            private readonly int[] _indexes;

            internal MergedRuntimeVariables(IRuntimeVariables first, IRuntimeVariables second, int[] indexes)
            {
                _first = first;
                _second = second;
                _indexes = indexes;
            }

            public int Count
            {
                get { return _indexes.Length; }
            }

            public object this[int index]
            {
                get
                {
                    index = _indexes[index];
                    return (index >= 0) ? _first[index] : _second[-1 - index];
                }
                set
                {
                    index = _indexes[index];
                    if (index >= 0)
                    {
                        _first[index] = value;
                    }
                    else
                    {
                        _second[-1 - index] = value;
                    }
                }
            }
        }
    }
}

#endif