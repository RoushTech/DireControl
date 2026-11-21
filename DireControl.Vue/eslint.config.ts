import { globalIgnores } from 'eslint/config'
import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript'
import pluginVue from 'eslint-plugin-vue'
import pluginOxlint from 'eslint-plugin-oxlint'
import skipFormatting from 'eslint-config-prettier/flat'
import type { Rule } from 'eslint'

/**
 * Icon-only buttons must carry an accessible name: a v-btn with
 * an `icon` prop and no `aria-label` (static or bound) is reported.
 */
const iconButtonLabel: Rule.RuleModule = {
  meta: { type: 'problem', messages: { missing: 'Icon-only v-btn needs an aria-label.' } },
  create(context) {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const services = context.sourceCode.parserServices as any
    if (!services?.defineTemplateBodyVisitor) return {}
    return services.defineTemplateBodyVisitor({
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      "VElement[rawName='v-btn']"(node: any) {
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const attrs: any[] = node.startTag.attributes
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const name = (a: any) =>
          a.directive ? (a.key.argument?.name ?? null) : (a.key.name ?? a.key.rawName)
        const hasIcon = attrs.some((a) => name(a) === 'icon')
        const hasLabel = attrs.some((a) => name(a) === 'aria-label' || name(a) === 'ariaLabel')
        if (hasIcon && !hasLabel) context.report({ node: node.startTag, messageId: 'missing' })
      },
    })
  },
}

// To allow more languages other than `ts` in `.vue` files, uncomment the following lines:
// import { configureVueProject } from '@vue/eslint-config-typescript'
// configureVueProject({ scriptLangs: ['ts', 'tsx'] })
// More info at https://github.com/vuejs/eslint-config-typescript/#advanced-setup

export default defineConfigWithVueTs(
  {
    name: 'app/files-to-lint',
    files: ['**/*.{vue,ts,mts,tsx}'],
  },

  globalIgnores(['**/dist/**', '**/dist-ssr/**', '**/coverage/**']),

  ...pluginVue.configs['flat/essential'],
  vueTsConfigs.recommended,

  ...pluginOxlint.buildFromOxlintConfigFile('.oxlintrc.json'),

  {
    name: 'app/conventions',
    files: ['src/**/*.{vue,ts}'],
    ignores: ['src/utils/time.ts'],
    plugins: { app: { rules: { 'icon-button-label': iconButtonLabel } } },
    rules: {
      'app/icon-button-label': 'error',
      // Every displayed time goes through utils/time.ts so the UTC/local
      // preference applies everywhere.
      'no-restricted-syntax': [
        'error',
        {
          selector: "CallExpression[callee.property.name=/^toLocale(Date|Time)String$/]",
          message: 'Format times with utils/time.ts (fmt helpers honour the UTC/local preference).',
        },
        {
          selector: "CallExpression[callee.property.name='toLocaleString'][arguments.length>0]",
          message: 'Format times with utils/time.ts; plain number toLocaleString() takes no arguments.',
        },
      ],
    },
  },

  skipFormatting,
)
