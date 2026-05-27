import tsPlugin from '@typescript-eslint/eslint-plugin';
import tsParser from '@typescript-eslint/parser';

export default [
  {
    ignores: ['docs/**', 'schema/**', 'scripts/**', 'plugins/**', 'node_modules/**', 'clients/typescript/**'],
  },
  {
    files: ['types/**/*.ts'],
    ignores: ['types/**/*.test.ts'],
    linterOptions: { reportUnusedDisableDirectives: 'off' },
    languageOptions: {
      parser: tsParser,
    },
    plugins: {
      '@typescript-eslint': tsPlugin,
    },
    rules: {
      '@typescript-eslint/consistent-type-assertions': ['error', { assertionStyle: 'never' }],
    },
  },
];
