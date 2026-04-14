# AI Project Naming Rules

You are working in the Citus repository.

Hard rules:

1. Do not create new root folders.
2. Do not invent new project categories.
3. All project names must follow:
   `Citus.<Category>[.<Module>][.<Layer>]`
4. Core accounting/business modules must use:
   `Citus.Modules.<Module>...`
5. External providers/integrations must use:
   `Citus.Connectors.<Provider>`
6. Allowed module layers are only:
   `Domain.Shared`, `Domain`, `Application.Contracts`, `Application`, `EntityFrameworkCore`, `Blazor`
7. Do not use names such as:
   `Common`, `Utils`, `Helpers`, `Manager`, `Processor`, `Temp`, `Misc`
8. Before generating code, first list the exact files and paths to create.
9. If no approved location fits, stop and report:
   `No approved target path found`