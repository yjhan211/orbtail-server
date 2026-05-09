// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    /// <summary>
    ///     부품 결합 레시피 (v0.2.0). 직책당 3 레시피 = 8 × 3 = 24 레시피.
    ///     소재 2개 → 중간재 / 중간재 2개 → 최종.
    /// </summary>
    public static class PartRecipeData
    {
        // 레시피 input → output 매핑
        private static readonly Dictionary<(int, int), PartRecipe> _recipesByInputs = new();
        // 직책별 레시피 목록
        private static readonly Dictionary<short, List<PartRecipe>> _recipesByJob = new();

        public static void Initialize(List<CsvRow> data)
        {
            _recipesByInputs.Clear();
            _recipesByJob.Clear();

            foreach (var row in data)
            {
                var recipe = new PartRecipe
                {
                    Id = int.Parse(row["id"]),
                    JobTitle = short.Parse(row["job_title"]),
                    InputPartA = int.Parse(row["input_part_a"]),
                    InputPartB = int.Parse(row["input_part_b"]),
                    OutputPart = int.Parse(row["output_part"]),
                    CombineDurationSeconds = int.Parse(row["combine_duration_seconds"])
                };

                // 양방향 매핑 (A+B와 B+A 모두 동일 결과)
                _recipesByInputs[(recipe.InputPartA, recipe.InputPartB)] = recipe;
                _recipesByInputs[(recipe.InputPartB, recipe.InputPartA)] = recipe;

                if (!_recipesByJob.ContainsKey(recipe.JobTitle))
                    _recipesByJob[recipe.JobTitle] = new List<PartRecipe>();
                _recipesByJob[recipe.JobTitle].Add(recipe);
            }
        }

        /// <summary>
        ///     두 부품 결합 시도 — 매칭 레시피 반환 (없으면 null)
        /// </summary>
        public static PartRecipe TryCombine(int partA, int partB) =>
            _recipesByInputs.GetValueOrDefault((partA, partB));

        /// <summary>
        ///     해당 직책의 모든 레시피
        /// </summary>
        public static List<PartRecipe> GetRecipes(short jobTitle) =>
            _recipesByJob.GetValueOrDefault(jobTitle) ?? new List<PartRecipe>();

        /// <summary>
        ///     특정 부품을 입력으로 사용하는 레시피 목록.
        /// </summary>
        public static List<PartRecipe> GetRecipesUsingInput(int partId) =>
            _recipesByJob.Values.SelectMany(recipes => recipes)
                .Where(recipe => recipe.InputPartA == partId || recipe.InputPartB == partId)
                .ToList();

        public static void Validate(managers.LogManager logger)
        {
            if (_recipesByJob.Count == 0)
                throw new InvalidOperationException("부품 레시피 데이터가 로드되지 않았습니다");

            foreach (var (jobTitle, recipes) in _recipesByJob)
            {
                if (recipes.Count != 3)
                    LogManager.WriteInfoLog($"[PartRecipeData] 직책 {jobTitle} 레시피 수 비정상: {recipes.Count}/3");
            }
        }
    }

    public class PartRecipe
    {
        public int Id { get; set; }
        public short JobTitle { get; set; }
        public int InputPartA { get; set; }
        public int InputPartB { get; set; }
        public int OutputPart { get; set; }
        public int CombineDurationSeconds { get; set; }
    }
}
