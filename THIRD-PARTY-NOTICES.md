# Third-party notices

## CASL

Parts of CASL .NET are translations of CASL's TypeScript source into C#, and its test suite includes tests
ported from CASL's specs. Each such file names the CASL file it comes from. They are:

- `src/Casl.Net/ActionAliases.cs`: `createAliasResolver` (`packages/casl-ability/src/utils.ts`)
- `src/Casl.Net/FieldPatterns.cs`: the field pattern matcher (`packages/casl-ability/src/matchers/field.ts`)
- `src/Casl.Net/PackedRules.cs`: `packRules` and `unpackRules` (`packages/casl-ability/src/extra/packRules.ts`)
- `src/Casl.Net/Ability.cs`: `rulesToCondition`, `permittedFieldsOf` and `rulesToFields` (`packages/casl-ability/src/extra/`)
- `tests/Casl.Net.Tests/CaslSpec/`: tests ported from `packages/casl-ability/spec/`

Source: https://github.com/stalniy/casl (commit e2b885a)

```
MIT License

Copyright (c) 2017-present Sergii Stotskyi

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
