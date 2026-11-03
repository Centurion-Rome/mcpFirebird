# Generic rules

## 1. Core principles

* Prefer the shortest diff that correctly solves the request.
* Follow YAGNI: no speculative abstractions, scaffolding, or new dependencies.
* Follow DRY, KISS and single responsibility.
* Use "one-liner" princip if possible
* use "ponytail" if present
* Reuse existing components, utilities, styles, and patterns.
* Do not refactor unrelated code or broaden task scope.
* Match surrounding code style, naming, formatting, brace style, and language.
* Do not commit or push until explicit asked for


## 2. Before changing code

* Inspect the relevant implementation and search for existing patterns/usages first.
* Follow established project conventions over generic framework recommendations.
* Do not assume behavior; verify it from the repository.
* When requirements are ambiguous, choose the least invasive interpretation.
* Avoid big refactoring

## 3. Dependencies and architecture

* Do not add dependencies unless explicitly requested.
* Do not introduce new architectural patterns or abstractions for local problems.
* Reuse existing services, helpers, base classes, and infrastructure.
* we can only use commerical-free packages (MIT-Licinsee) if absollitely neccessery 

## 4. Safety and verification

* Preserve existing data, persistence, sync, authentication, and error-handling behavior unless the task requires changing it.
* Do not manually edit generated files unless necessary.
* Inspect the final diff and run the smallest relevant build/test/validation command.
* Do not claim something was tested if it was not actually tested.
* Any safety or very importand open tasks or apkets that are not but should be implemented should be writen in TODO.md in main dir with short description so we don forget them

---

# Project-specific rules
First read project goal in file GOAL.md

## 1. Code formatting
* `.editorconfig` at solution root is source of truth. Follow it.
* Default: TABS, width 2. SQL (`*.sql`) / WebForms (`*.aspx,*.ascx`): TABS, width 4. YAML (`*.yml,*.yaml`): SPACES, width 2 — only spaces exception.
* Copy exact whitespace of the file being edited. Never reformat the whole file, never convert tabs<->spaces, never change line endings.
* New files: tabs (YAML: spaces). One tab per indent level (YAML: 2 spaces per level). 
- Copy the exact whitespace of the file being edited. Never reformat a whole
  file, never convert tabs↔spaces, never change line endings.
- C# naming (suggestion-level rules in `.editorconfig`): interfaces `I`-prefix,
  types/methods/properties PascalCase.

## Architecture
- implementation in C# 

More info hier need to be updated