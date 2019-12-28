using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using CsvHelper;
using CsvHelper.Configuration.Attributes;

namespace HarvestToGephi
{
    class Program
    {
        private const string GephiDateFormat = "yyyy-MM-dd";
        public static bool IncludeProjects = false;
        public static bool Scramble = true;

        static void Main(string[] args)
        {
            if (args.Contains("p")) IncludeProjects = true;
            const string csvFile = @"c:\users\sunny\downloads\harvest_2010_to_2019.csv";
            List<HarvestExport> records;
            using var reader = new StreamReader(csvFile);
            using var csv = new CsvReader(reader);

            records = csv.GetRecords<HarvestExport>().ToList();
            Console.WriteLine($"Initially got {records.Count} records");

            records = records.Where(r => !r.Client.Contains("IgNew", StringComparison.InvariantCultureIgnoreCase))
                .ToList();
            Console.WriteLine($"Filtered down to {records.Count} records");

            int id = 1;

            var clients = records.Select(r => r.Client).Distinct().Select(c => new Client
            {
                Name = c,
                Id = id++,
                StartDate = records.Where(r => r.Client == c).Min(r => r.Date),
                EndDate = records.Where(r => r.Client == c).Max(r => r.Date),
                Hours = records.Where(r => r.Client == c).Sum(r => r.Hours)
            })
                .ToDictionary(x => x.Name);
            Console.WriteLine($"Extracted {clients.Count} clients");

            var projects = records.Select(r => new { r.Client, r.Project }).Distinct().Select(r => new Project
            {
                Id = id++,
                Client = r.Client,
                Name = r.Project,
                StartDate = records.Where(x => r.Client == x.Client && r.Project == x.Project).Min(x => x.Date),
                EndDate = records.Where(x => r.Client == x.Client && r.Project == x.Project).Max(x => x.Date),
                Hours = records.Where(x => r.Client == x.Client && r.Project == x.Project).Sum(x => x.Hours)
            }).ToDictionary(x => new { x.Client, Project = x.Name });
            Console.WriteLine($"Extracted {projects.Count} projects");

            var persons = records.Select(r => new { r.FirstName, r.LastName }).Distinct().Select(r => new Person
            {
                Id = id++,
                FirstName = r.FirstName,
                LastName = r.LastName,
                StartDate = records.Where(x => r.FirstName == x.FirstName && r.LastName == x.LastName).Min(x => x.Date),
                EndDate = records.Where(x => r.FirstName == x.FirstName && r.LastName == x.LastName).Max(x => x.Date),
            }).ToDictionary(x => new { x.FirstName, x.LastName });
            Console.WriteLine($"Extracted {persons.Count} persons");

            Console.WriteLine("Combining Nodes");
            var nodes = clients.Select(c => new Node()
            {
                Id = c.Value.Id,
                Label = Scramble ?
                    FirstThree(c.Value.Name)
                    : c.Value.Name,
                Noun = "Client",
                StartDate = c.Value.StartDate.ToString(GephiDateFormat),
                EndDate = c.Value.EndDate.ToString(GephiDateFormat),
                Size = c.Value.Hours
            }).Union(persons.Select(u => new Node()
            {
                Id = u.Value.Id,
                Label = Scramble ?
                    u.Value.FirstName.Substring(0, 1) + u.Value.LastName.Substring(0, 1)
                    : u.Value.FirstName + " " + u.Value.LastName.Substring(0, 1),
                Noun = "Person",
                StartDate = u.Value.StartDate.ToString(GephiDateFormat),
                EndDate = u.Value.EndDate.ToString(GephiDateFormat)
            })).ToList();

            if (IncludeProjects)
                nodes.AddRange(projects.Select(p => new Node()
                {
                    Id = p.Value.Id,
                    Label = Scramble?
                        FirstThree(p.Value.Name)
                        :p.Value.Name,
                    Noun = "Project",
                    StartDate = p.Value.StartDate.ToString(GephiDateFormat),
                    EndDate = p.Value.EndDate.ToString(GephiDateFormat),
                    Size = p.Value.Hours
                }));

            var minDate = records.Min(r => r.Date);
            var maxDate = records.Max(r => r.Date);
            var timeSpan = (maxDate - minDate) / 200;  // match this to animation speed of 1%, maybe double that. 

            Console.WriteLine("Getting Person interactions...");
            var userEdges = new List<Edge>();

            for (var startTime = minDate; startTime < maxDate; startTime += timeSpan)
            {
                Console.Write(".");

                foreach (var u in persons.Values)
                {
                    var endTime = startTime + timeSpan;

                    var personRecords = records
                        .Where(x => x.FirstName == u.FirstName && x.LastName == u.LastName && x.Date >= startTime
                                    &&
                                    x.Date <= endTime).ToList();

                    if (personRecords.Count == 0) continue;

                    var personTotalHours = personRecords.Sum(h => h.Hours);

                    if (IncludeProjects)
                    {
                        var g1 = personRecords.GroupBy(x => new { x.Client, x.Project }).ToList();

                        foreach (var g in g1)
                        {
                            var project = projects[g.Key];

                            var sumHours = g.Sum(x => x.Hours);

                            if (sumHours < personTotalHours / 8) continue;

                            userEdges.Add(new Edge()
                            {
                                // attraction algorithm distributes weights by inbound edges
                                // so if 2 projects each have 1 user, then user->project causes user to be sucked into both
                                // so we have to do project->user so that user only gets sucked into a project if it is their sole focus
                                // or we just do undirected.
                                Target = u.Id,
                                Source = project.Id,
                                StartDate = startTime
                                    .ToString(GephiDateFormat),
                                EndDate = endTime.ToString(GephiDateFormat),
                                Verb = "worked",
                                Weight = g.Sum(x => x.Hours),
                                Type = "undirected"
                            });
                        }
                    }
                    else
                    {
                        var g1 = personRecords.GroupBy(x => x.Client).ToList();

                        foreach (var g in g1)
                        {
                            var client = clients[g.Key];

                            userEdges.Add(new Edge()
                            {
                                Target = u.Id,
                                Source = client.Id,
                                StartDate = startTime.ToString(GephiDateFormat),
                                EndDate = endTime.ToString(GephiDateFormat),
                                Verb = "worked",
                                Weight = g.Sum(x => x.Hours),
                                Type = "undirected"
                            });
                        }
                    }
                }
            }

            Console.WriteLine();

            var edges = userEdges.ToList();

            if (IncludeProjects)
            {
                edges.AddRange(projects.Select(p => new Edge()
                {
                    Verb = "project",
                    StartDate = p.Value.StartDate.ToString(GephiDateFormat),
                    EndDate = p.Value.EndDate.ToString(GephiDateFormat),
                    Target = clients[p.Value.Client].Id,
                    Source = p.Value.Id,
                    Type = "undirected",
                    Weight = p.Value.Hours
                }).ToList());
            }

            using (var writer = new StreamWriter("harvestNodes.csv"))
            using (var csvw = new CsvWriter(writer))
                csvw.WriteRecords(nodes);
            Console.WriteLine($"Wrote {nodes.Count} nodes");

            using (var writer = new StreamWriter("harvestEdges.csv"))
            using (var csvw = new CsvWriter(writer))
                csvw.WriteRecords(edges);
            Console.WriteLine($"Wrote {edges.Count} edges");

        }

        private static string FirstThree(string name)
        {
            return name.PadRight(3).Substring(0, 3).Trim();
        }

        public class Node
        {
            public int Id { get; set; }
            public string Label { get; set; }
            public string Noun { get; set; }
            public string StartDate { get; set; }
            public string EndDate { get; set; }
            public decimal Size { get; set; }
        }

        public class Edge
        {
            public int Source { get; set; }
            public int Target { get; set; }
            public string Verb { get; set; }
            public string StartDate { get; set; }
            public string EndDate { get; set; }
            public decimal Weight { get; set; }
            public string Type { get; set; }
        }

        public class HarvestExport
        {
            public string Client { get; set; }
            public string Project { get; set; }
            [Name("First Name")]
            public string FirstName { get; set; }
            [Name("Last Name")]
            public string LastName { get; set; }
            public DateTime Date { get; set; }
            public decimal Hours { get; set; }
        }

        public class Client
        {
            public string Name { get; set; }
            public int Id { get; set; }
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
            public decimal Hours { get; set; }
        }

        public class Project
        {
            public int Id { get; set; }
            public string Client { get; set; }
            public string Name { get; set; }
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
            public decimal Hours { get; set; }
        }

        public class Person
        {
            public int Id { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
        }
    }
}
