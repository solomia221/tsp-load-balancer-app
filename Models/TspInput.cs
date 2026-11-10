namespace TSP.Backend.Models;

public record TspInput(double[][] DistanceMatrix, int Generations, int PopulationSize);