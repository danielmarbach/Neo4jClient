﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Neo4jClient.Gremlin
{
    internal static class FilterFormatters
    {
        internal static FormattedFilter FormatGremlinFilter(IEnumerable<Filter> filters, StringComparison comparison, IGremlinQuery queryThatTheFilterWillEventuallyBeAddedTo)
        {
            filters = filters
                .Select(f =>
                {
                    if (f.Value != null && f.Value.GetType().IsEnum)
                        f.Value = f.Value.ToString();
                    return f;
                })
                .ToArray();

            if (filters.Any(f => !f.ExpressionType.HasValue))
                throw new ArgumentException("ExpressionType must not be null", "filters");

            var typeFilterFormats = new List<TypeFilter>
            {
                new TypeFilter { Type = null, FilterFormat = "it[{0}] == null", ExpressionType = ExpressionType.Equal },
                new TypeFilter { Type = null, FilterFormat = "it[{0}] != null", ExpressionType = ExpressionType.NotEqual },
                new TypeFilter { Type = typeof(int), FilterFormat = "it[{0}] == {1}", ExpressionType = ExpressionType.Equal },
                new TypeFilter { Type = typeof(int), FilterFormat = "it[{0}] != {1}", ExpressionType = ExpressionType.NotEqual },
                new TypeFilter { Type = typeof(int), FilterFormat = "it[{0}] > {1}", ExpressionType = ExpressionType.GreaterThan},
                new TypeFilter { Type = typeof(int), FilterFormat = "it[{0}] < {1}", ExpressionType = ExpressionType.LessThan},
                new TypeFilter { Type = typeof(int), FilterFormat = "it[{0}] >= {1}", ExpressionType = ExpressionType.GreaterThanOrEqual},
                new TypeFilter { Type = typeof(int), FilterFormat = "it[{0}] <= {1}", ExpressionType = ExpressionType.LessThanOrEqual},
                new TypeFilter { Type = typeof(long), FilterFormat = "it[{0}] == {1}", ExpressionType = ExpressionType.Equal },
                new TypeFilter { Type = typeof(long), FilterFormat = "it[{0}] != {1}", ExpressionType = ExpressionType.NotEqual },
                new TypeFilter { Type = typeof(long), FilterFormat = "it[{0}] > {1}", ExpressionType = ExpressionType.GreaterThan},
                new TypeFilter { Type = typeof(long), FilterFormat = "it[{0}] < {1}", ExpressionType = ExpressionType.LessThan},
                new TypeFilter { Type = typeof(long), FilterFormat = "it[{0}] >= {1}", ExpressionType = ExpressionType.GreaterThanOrEqual},
                new TypeFilter { Type = typeof(long), FilterFormat = "it[{0}] <= {1}", ExpressionType = ExpressionType.LessThanOrEqual},
                new TypeFilter { Type = typeof(bool), FilterFormat = "it[{0}] == {1}", ExpressionType = ExpressionType.Equal },
                new TypeFilter { Type = typeof(bool), FilterFormat = "it[{0}] != {1}", ExpressionType = ExpressionType.NotEqual },
                new TypeFilter { Type = typeof(Guid), FilterFormat = "it[{0}] == {1}", ExpressionType = ExpressionType.Equal },
                new TypeFilter { Type = typeof(Guid), FilterFormat = "it[{0}] != {1}", ExpressionType = ExpressionType.NotEqual },
            };

            const string filterSeparator = " && ";
            const string concatenatedFiltersFormat = "{{ {0} }}";

            switch (comparison)
            {
                case StringComparison.Ordinal:
                        typeFilterFormats.Add(new TypeFilter { Type = typeof(string), FilterFormat = "it[{0}].equals({1})", ExpressionType = ExpressionType.Equal});
                        typeFilterFormats.Add(new TypeFilter { Type = typeof(string), FilterFormat = "!it[{0}].equals({1})", ExpressionType = ExpressionType.NotEqual  });
                    break;
                case StringComparison.OrdinalIgnoreCase:
                        typeFilterFormats.Add(new TypeFilter { Type = typeof(string), FilterFormat = "it[{0}].equalsIgnoreCase({1})", ExpressionType = ExpressionType.Equal  });
                        typeFilterFormats.Add(new TypeFilter { Type = typeof(string), FilterFormat = "!it[{0}].equalsIgnoreCase({1})", ExpressionType = ExpressionType.NotEqual  });
                    break;
                default:
                    throw new NotSupportedException(string.Format("Comparison mode {0} is not supported.", comparison));
            }

            var parameters = new Dictionary<string, object>();
            var nextParameterIndex = queryThatTheFilterWillEventuallyBeAddedTo.QueryParameters.Count;
            Func<object, string> createParameter = value =>
                {
                    if (value == null) return "null";
                    var paramName = string.Format("p{0}", nextParameterIndex);
                    parameters.Add(paramName, value);
                    nextParameterIndex++;
                    return paramName;
                };

            var expandedFilters =
                from f in filters
                let filterValueType = f.Value == null ? null : f.Value.GetType()
                let typeFilter = typeFilterFormats.SingleOrDefault(tf => tf.Type == filterValueType && tf.ExpressionType == f.ExpressionType)
                let isFilterSupported = typeFilter != null
                let filterFormat = isFilterSupported ? typeFilter.FilterFormat : null
                select new
                {
                    f.PropertyName,
                    PropertyNameParam = createParameter(f.PropertyName),
                    ValueParam = createParameter(f.Value),
                    f.ExpressionType,
                    IsFilterSupported = isFilterSupported,
                    ValueType = filterValueType,
                    Format = filterFormat
                };

            expandedFilters = expandedFilters.ToArray();

            var unsupportedFilters = expandedFilters
                .Where(f => !f.IsFilterSupported)
                .Select(f => f.ValueType == null
                    ? string.Format("{0} with null value and expression {1}", f.PropertyName, f.ExpressionType)
                    : string.Format("{0} of type {1}, with expression {2}", f.PropertyName, f.ValueType.FullName, f.ExpressionType))
                .ToArray();
            if (unsupportedFilters.Any())
                throw new NotSupportedException(string.Format(
                    "One or more of the supplied filters is of an unsupported type or expression. Unsupported filters were: {0}",
                    string.Join(", ", unsupportedFilters)));

            var formattedFilters = expandedFilters
                .Select(f => string.Format(f.Format, f.PropertyNameParam, f.ValueParam))
                .ToArray();
            var concatenatedFilters = string.Join(filterSeparator, formattedFilters);
            if (!string.IsNullOrWhiteSpace(concatenatedFilters))
                concatenatedFilters = string.Format(concatenatedFiltersFormat, concatenatedFilters);

            var filter = string.IsNullOrWhiteSpace(concatenatedFilters) ? string.Empty : ".filter";

            return new FormattedFilter
            {
                FilterText = filter + concatenatedFilters,
                FilterParameters = parameters
            };
        }

        internal static IEnumerable<Filter> TranslateFilter<TNode>(Expression<Func<TNode, bool>> filter)
        {
            var binaryExpression = filter.Body as BinaryExpression;
            if (binaryExpression == null)
                throw new NotSupportedException("Only binary expressions are supported at this time.");

            var key = ParseKeyFromExpression(binaryExpression.Left);
            var constantValue = ParseValueFromExpression(binaryExpression.Right);

            var underlyingPropertyType = key.PropertyType;
            underlyingPropertyType = Nullable.GetUnderlyingType(key.PropertyType) ?? underlyingPropertyType;

            var convertedValue = underlyingPropertyType.IsEnum
                ? Enum.Parse(underlyingPropertyType, underlyingPropertyType.GetEnumName(constantValue))
                : constantValue;

            return new[]
            {
                new Filter
                {
                    PropertyName = key.Name,
                    Value = convertedValue,
                    ExpressionType = binaryExpression.NodeType
                }
            };
        }

        internal static ExpressionKey ParseKeyFromExpression(Expression expression)
        {
            var unaryExpression = expression as UnaryExpression;
            if (unaryExpression != null &&
                unaryExpression.NodeType == ExpressionType.Convert)
                expression = unaryExpression.Operand;

            var memberExpression = expression as MemberExpression;
            if (memberExpression != null &&
                memberExpression.Member is PropertyInfo &&
                memberExpression.Member.MemberType == MemberTypes.Property)
                return new ExpressionKey
                {
                    Name = memberExpression.Member.Name,
                    PropertyType = ((PropertyInfo)memberExpression.Member).PropertyType
                };

            throw new NotSupportedException("Only property accessors are supported for the left-hand side of the expression at this time.");
        }

        static object ParseValueFromExpression(Expression expression)
        {
            var lambdaExpression = Expression.Lambda(expression);
            return lambdaExpression.Compile().DynamicInvoke();
        }

        internal class ExpressionKey
        {
            public string Name { get; set; }
            public Type PropertyType { get; set; }
        }
    }
}
