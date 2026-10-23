// @ts-check
const eslint = require('@eslint/js');
const { defineConfig } = require('eslint/config');
const tseslint = require('typescript-eslint');
const angular = require('angular-eslint');

const TRANSLATED_TEMPLATES = [
  'src/app/app.html',
  'src/app/assistant/assistant-panel.html',
  'src/app/shared/command-palette/command-palette.html',
  'src/app/shared/drained-indicator/drained-indicator.ts/*.html',
  'src/app/shared/output-pane/output-pane.ts/*.html',
  'src/app/workspace/workspace-grid.html',
  'src/app/workspace/widgets/note/mermaid-diagram.ts/*.html',
  'src/app/workspace/widgets/note/note-widget.ts/*.html',
];

module.exports = defineConfig([
  {
    files: ['**/*.ts'],
    extends: [
      eslint.configs.recommended,
      tseslint.configs.recommended,
      tseslint.configs.stylistic,
      angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      '@angular-eslint/directive-selector': [
        'error',
        {
          type: 'attribute',
          prefix: 'app',
          style: 'camelCase',
        },
      ],
      '@angular-eslint/component-selector': [
        'error',
        {
          type: 'element',
          prefix: 'app',
          style: 'kebab-case',
        },
      ],
    },
  },
  {
    files: ['**/*.html'],
    extends: [angular.configs.templateRecommended, angular.configs.templateAccessibility],
    rules: {},
  },
  {
    // The templates translated so far (AI assistant design §5.5): a literal in one of them without an i18n marker
    // would ship English in every language. The list grows with each workspace phase, and the final sweep replaces
    // it with every template. An inline template is linted as `<component>.ts/<n>_inline-template...html`.
    files: TRANSLATED_TEMPLATES,
    rules: {
      '@angular-eslint/template/i18n': [
        'error',
        // An icon's ligature and a key cap are names, not words; these attributes hold a token or an element's id.
        {
          checkId: false,
          ignoreTags: ['mat-icon', 'kbd'],
          ignoreAttributes: [
            'accept',
            'aria-autocomplete',
            'aria-controls',
            'aria-describedby',
            'aria-labelledby',
            'aria-live',
            'mode',
            'position',
          ],
        },
      ],
    },
  },
]);
