using System;
using System.Collections.Generic;
using System.Linq;

namespace TSP.Backend.Services
{
    public class TspSolverService
    {
        private readonly Random _rand = new Random();

        // 1. Генерація початкової популяції маршрутів
        public List<List<int>> GenerateInitialPopulation(int populationSize, int citiesCount)
        {
            var population = new List<List<int>>();
            var baseRoute = Enumerable.Range(0, citiesCount).ToList();

            for (int i = 0; i < populationSize; i++)
            {
                var shuffledRoute = baseRoute.OrderBy(_ => _rand.Next()).ToList();
                population.Add(shuffledRoute);
            }

            return population;
        }

        // 2. Обчислення загальної довжини маршруту
        public double CalculateTotalDistance(List<int> route, double[,] distanceMatrix)
        {
            double totalDistance = 0;
            for (int i = 0; i < route.Count - 1; i++)
            {
                totalDistance += distanceMatrix[route[i], route[i + 1]];
            }
            totalDistance += distanceMatrix[route.Last(), route.First()];
            return totalDistance;
        }

        // 3. Функція схрещування (Ordered Crossover - OX)
        public List<int> Crossover(List<int> parent1, List<int> parent2)
        {
            int length = parent1.Count;
            var child = Enumerable.Repeat(-1, length).ToList();

            int start = _rand.Next(length);
            int end = _rand.Next(length);

            if (start > end) (start, end) = (end, start);

            for (int i = start; i <= end; i++)
            {
                child[i] = parent1[i];
            }

            int currentIndex = (end + 1) % length;
            for (int i = 0; i < length; i++)
            {
                int parent2Index = (end + 1 + i) % length;
                int city = parent2[parent2Index];

                if (!child.Contains(city))
                {
                    child[currentIndex] = city;
                    currentIndex = (currentIndex + 1) % length;
                }
            }

            return child;
        }

        // 4. Функція мутації (Swap Mutation)
        public void Mutate(List<int> route, double mutationRate)
        {
            for (int i = 0; i < route.Count; i++)
            {
                if (_rand.NextDouble() < mutationRate)
                {
                    int j = _rand.Next(route.Count);
                    (route[i], route[j]) = (route[j], route[i]);
                }
            }
        }

        // 5. Механізм запуску алгоритму з передачею поточного прогресу (0–100%)
        public List<int> SolveTsp(double[,] distanceMatrix, int generations = 100, int populationSize = 50, double mutationRate = 0.1, Action<int>? reportProgress = null)
        {
            int citiesCount = distanceMatrix.GetLength(0);
            var population = GenerateInitialPopulation(populationSize, citiesCount);

            List<int> bestRoute = population[0];
            double bestDistance = CalculateTotalDistance(bestRoute, distanceMatrix);

            for (int gen = 0; gen < generations; gen++)
            {
                // Сортуємо популяцію за довжиною маршруту (найкращі зверху)
                population = population.OrderBy(r => CalculateTotalDistance(r, distanceMatrix)).ToList();

                double currentBestDistance = CalculateTotalDistance(population[0], distanceMatrix);
                if (currentBestDistance < bestDistance)
                {
                    bestDistance = currentBestDistance;
                    bestRoute = new List<int>(population[0]);
                }

                var newPopulation = new List<List<int>>
                {
                    new List<int>(population[0]) // Елітизм
                };

                while (newPopulation.Count < populationSize)
                {
                    var parent1 = population[_rand.Next(populationSize / 2)];
                    var parent2 = population[_rand.Next(populationSize / 2)];

                    var child = Crossover(parent1, parent2);
                    Mutate(child, mutationRate);
                    newPopulation.Add(child);
                }

                population = newPopulation;

                // Вираховуємо та передаємо прогрес у відсотках (0 - 100%)
                if (reportProgress != null && generations > 0)
                {
                    int percent = (int)((gen + 1) / (double)generations * 100);
                    reportProgress(percent);
                }
            }

            return bestRoute;
        }
    }
}