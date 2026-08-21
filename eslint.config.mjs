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
  {
    files: ['types/**/*reducer*.ts'],
    ignores: ['types/**/*.test.ts'],
    rules: {
      'no-restricted-globals': ['error', {
        name: 'Date',
        message: 'Reducers must be deterministic. Put timestamps on actions or derive them from action data.',
      }],
    },
  },
];
