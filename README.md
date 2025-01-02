# Digdir BOD Roadmap Report

This is a API that generates a report based on the [Digdir Roadmap](https://github.com/orgs/digdir/projects/8), which uses the Github GraphQL API to fetch all issues related to "Nye Altinn" and generates success indicator values based on the start, end and closed dates and progression of the issues. This is returned as a JSON array of objects, which can be imported into PowerBI et. al. for further analysis.

## Local installation

1. Clone the repo
2. Navigate to the project directory
3. `dotnet user-secrets set GitHubToken <your-github-token>`
4. Run the application (`dotnet run`)
5. Open `https://localhost:7127/report` in your browser (or import it to Excel via PowerQuery)

## Importing via PowerQuery to Excel

1. Open a new Excel document
2. Go to `Data` -> `Get Data (Power Query)`
3. Select `Empty Query`
4. Paste the following code into the editor:

```
let
  Source = Json.Document(Web.Contents("https://localhost:7127/report")),
  AsTable = Table.FromRecords(Source)
in
  AsTable
```

5. Click `Close & Load` to import the data

Alternatively, you can use `File.Contents("/path/to/report.json")` to import a local file.