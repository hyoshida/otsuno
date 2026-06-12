---
description: Coding style guidelines for this project
---

# Coding Style

## 1. Keep it concise

- Omit unnecessary syntax wherever possible.
  - `List<any> list = new List<any>()` => `List<any> list = new()`
  - `string text = ...` => `var text = ...`
  - `private bool flag = false;` => `bool flag;`
- However, avoid writing styles that become too complex or hurt readability.
  - Do not overuse inline methods.
  - Do not abbreviate variable names: `Obj` => `Object`, `Idx` => `Index` or `i`
- Extract and reuse shared logic whenever possible.
- Keep short positional records and collection expressions on one line when they remain readable.
  - `public record Item(string Name, int Count);`
  - `protected static readonly Item[] Items = [`
- For multi-line method or constructor calls, put the closing parenthesis on its own line and do not leave a trailing comma after the final argument.

## 2. Split logic into small methods when possible

- When editing elements in a loop, separate the looping logic from the element-editing logic.
- Use method names that clearly describe the behavior so comments are not required.

# Architecture

## 1. Do Not Modify Libraries

- Libraries may be updated in the future, so even when behavior changes are required, do not edit them directly.
- Instead, interfere indirectly by using wrappers or similar approaches.
- The specific libraries in this project are as follows:
  - `Assets/CorgiEngine/*`
  - `Assets/CorgiEngineExtentions/*`

## 2. Follow the SOLID Principles

- Avoid mutual dependencies.
- A single class should have a single responsibility, and each method should be short and handle only one process.
- Prioritize ease of inheritance and extensibility, and avoid using `private` for methods and variables unless necessary; use `protected virtual` and `protected` whenever possible.
