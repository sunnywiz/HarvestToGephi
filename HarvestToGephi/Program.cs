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

        static void Main(string[] args)
        {
            if (args.Contains("p")) IncludeProjects=true; 
            const string csvFile = @"c:\users\sunny\downloads\harvest_time_report_from2018-01-01to2019-12-20.csv";
            List<HarvestExport> records;
            using var reader = new StreamReader(csvFile);
            using var csv = new CsvReader(reader);

            records = csv.GetRecords<HarvestExport>().ToList();

            records = records.Where(r => !r.Client.Contains("IgNew", StringComparison.InvariantCultureIgnoreCase))
                .ToList(); 

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

            var projects = records.Select(r => new {r.Client, r.Project}).Distinct().Select(r => new Project
            {
                Id = id++,
                Client = r.Client,
                Name = r.Project,
                StartDate = records.Where(x => r.Client == x.Client && r.Project == x.Project).Min(x => x.Date),
                EndDate = records.Where(x => r.Client == x.Client && r.Project == x.Project).Max(x => x.Date),
                Hours = records.Where(x => r.Client == x.Client && r.Project == x.Project).Sum(x => x.Hours)
            }).ToDictionary(x => new {x.Client, Project = x.Name});

            var users = records.Select(r => new {r.FirstName, r.LastName}).Distinct().Select(r => new User
            {
                Id = id++, 
                FirstName = r.FirstName, 
                LastName = r.LastName,
                StartDate = records.Where(x => r.FirstName== x.FirstName && r.LastName== x.LastName).Min(x => x.Date),
                EndDate = records.Where(x => r.FirstName== x.FirstName && r.LastName== x.LastName).Max(x => x.Date),
            }).ToDictionary(x => new {x.FirstName, x.LastName});

            var nodes = clients.Select(c => new Node()
            {
                Id = c.Value.Id,
                Label = c.Value.Name,
                Noun = "Client",
                StartDate = c.Value.StartDate.ToString(GephiDateFormat),
                EndDate = c.Value.EndDate.ToString(GephiDateFormat),
                Size = c.Value.Hours
            }).Union(users.Select(u=>new Node()
            {
                Id = u.Value.Id,
                Label = u.Value.FirstName+" "+u.Value.LastName,
                Noun = "User",
                StartDate = u.Value.StartDate.ToString(GephiDateFormat),
                EndDate = u.Value.EndDate.ToString(GephiDateFormat)
            })).ToList();

            if (IncludeProjects)
                nodes.AddRange(projects.Select(p => new Node()
                {
                    Id = p.Value.Id,
                    Label = p.Value.Name,
                    Noun = "Project",
                    StartDate = p.Value.StartDate.ToString(GephiDateFormat),
                    EndDate = p.Value.EndDate.ToString(GephiDateFormat),
                    Size = p.Value.Hours
                }));

            var projectEdges = projects.Select(p => new Edge()
            {
                Verb = "project",
                StartDate = p.Value.StartDate.ToString(GephiDateFormat),
                EndDate = p.Value.EndDate.ToString(GephiDateFormat),
                Source = clients[p.Value.Client].Id,
                Target = p.Value.Id
            }).ToList();

            var minDate = records.Min(r => r.Date);
            var maxDate = records.Max(r => r.Date);
            var timeSpan = new TimeSpan(7,0,0,0);

            var userEdges = new List<Edge>(); 
            for (var t = minDate; t < maxDate; t += timeSpan)
            {
                foreach (var u in users.Values)
                {
                    var t2 = t + timeSpan;

                    var hours = records
                        .Where(x => x.FirstName == u.FirstName && x.LastName == u.LastName && x.Date >= t &&
                                    x.Date < t2).ToList();
                    if (hours.Count == 0) continue;

                    if (IncludeProjects)
                    {
                        var g1 = hours.GroupBy(x => new {x.Client, x.Project}).ToList();

                        foreach (var g in g1)
                        {
                            var client = clients[g.Key.Client];
                            var project = projects[g.Key];

                            userEdges.Add(new Edge()
                            {
                                Source = u.Id,
                                Target = project.Id,
                                StartDate = t.ToString(GephiDateFormat),
                                EndDate = t2.ToString(GephiDateFormat),
                                Verb = "worked", 
                                Weight = g.Sum(x=>x.Hours)
                            });
                        }
                    } else
                    {
                        var g1 = hours.GroupBy(x => x.Client).ToList();

                        foreach (var g in g1)
                        {
                            var client = clients[g.Key];

                            userEdges.Add(new Edge()
                            {
                                Source = u.Id,
                                Target = client.Id,
                                StartDate = t.ToString(GephiDateFormat),
                                EndDate = t2.ToString(GephiDateFormat),
                                Verb = "worked",
                                Weight = g.Sum(x=>x.Hours)
                            });
                        }
                    }
                }
            }

            var edges = userEdges.ToList();
            if (IncludeProjects) edges.AddRange(projectEdges);

            using (var writer = new StreamWriter("harvestNodes.csv"))
            using (var csvw = new CsvWriter(writer))
                csvw.WriteRecords(nodes);
            Console.WriteLine($"Wrote {nodes.Count} nodes");

            using (var writer = new StreamWriter("harvestEdges.csv"))
            using (var csvw = new CsvWriter(writer))
                csvw.WriteRecords(edges);
            Console.WriteLine($"Wrote {edges.Count} edges");

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
        }

        public class HarvestExport
        {
            public string Client { get; set;  }
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

        public class User
        {
            public int Id { get; set; }
            public string FirstName { get; set;  }
            public string LastName { get; set;  }
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
        }
    }
}
