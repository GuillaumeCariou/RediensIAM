import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  // coverage/ is Istanbul's generated HTML report. It ships its own vendored scripts, each
  // carrying eslint directives for a config that is not this one, so linting it reports warnings
  // about suppressions nobody here wrote.
  globalIgnores(['dist', 'coverage']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
    },
    rules: {
      // A leading underscore is this codebase's way of saying "this parameter exists to hold a
      // position, and is deliberately not read" — a mock that must accept the argument its real
      // counterpart receives, a `describe.each` label consumed by the title. The default rule has
      // no such convention and reported each of them as an error, so the build failed on code that
      // was doing the right thing. Only ARGUMENTS are exempted: an unused local variable is still
      // dead code, and stays an error.
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
    },
  },
])
