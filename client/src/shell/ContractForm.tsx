import { useId } from 'react';
import { NumberInput, Select, Switch, Text, TextInput } from '@mantine/core';
import type { ContractDefinition, FieldSchema, ValueSchema } from '../platform/contracts';
import { IsoDateInput } from '../platform/IsoDateInput';

/**
 * How the fields are laid out (docs/design-guidelines.md, "Forms and toolbars"):
 * - `stacked`: a form in a dialog or a page. The label sits above its input and the message below it.
 * - `toolbar`: a row of controls, such as the header's context. The label sits to the left of its input, every
 *   control is one Control Height tall, and a message is not drawn under the input, because it would change the row's
 *   height: the input is marked invalid, and whoever lays out the toolbar says the message elsewhere.
 */
export type FieldLayout = 'stacked' | 'toolbar';

/** Fields for a contract's schema, one approved input per field type. No field is invented. */
export function ContractFields({ contract, values, errors, onChange, autoFocus, layout = 'stacked' }: {
  readonly contract: ContractDefinition;
  readonly values: Readonly<Record<string, unknown>>;
  readonly errors: Readonly<Record<string, string>>;
  readonly onChange: (field: string, value: unknown) => void;
  readonly autoFocus?: boolean;
  readonly layout?: FieldLayout;
}) {
  return Object.entries(contract.schema.properties).map(([name, field], index) => (
    <Field key={name} name={name} field={field} value={values[name]} error={errors[name]}
      required={contract.schema.required?.includes(name) === true}
      onChange={(v) => onChange(name, v)} autoFocus={autoFocus === true && index === 0} layout={layout} />
  ));
}

function Field({ name, field, value, error, required, onChange, autoFocus, layout }: {
  readonly name: string;
  readonly field: FieldSchema;
  readonly value: unknown;
  readonly error: string | undefined;
  readonly required: boolean;
  readonly onChange: (value: unknown) => void;
  readonly autoFocus: boolean;
  readonly layout: FieldLayout;
}) {
  const id = useId();
  const label = field.title ?? name;
  // A form draws a control for one value. The server registers no workflow whose input holds a list (ADR-0016 note
  // 37); one met here is said where its control would be, never left out.
  if (field.type === 'array') return <Text size="sm" c="red" role="alert">{label} is a list, which a form does not draw.</Text>;
  if (field.type === 'boolean')
    return <Switch label={label} checked={value === true} onChange={(e) => onChange(e.currentTarget.checked)}
      size={layout === 'toolbar' ? 'xs' : 'sm'} />;

  const input = layout === 'toolbar'
    ? { id, size: 'xs', error: error !== undefined, className: `qube-toolbar-input qube-toolbar-input--${kind(field)}` } as const
    : { id, size: 'sm', label, error } as const;
  const common = { ...input, 'data-autofocus': autoFocus || undefined } as const;

  const control = field.type === 'string' && field.enum !== undefined
    ? <Select {...common} data={[...field.enum]} value={typeof value === 'string' ? value : null}
        onChange={(v) => onChange(v ?? undefined)} allowDeselect={false} comboboxProps={{ withinPortal: true }} />
    // A date reads yyyy-MM-dd whatever the browser's locale; only a date the contract does not require is cleared.
    : field.type === 'string' && field.format === 'date'
      ? (required
        ? <IsoDateInput {...common} value={typeof value === 'string' ? value : undefined} onChange={onChange} />
        : <IsoDateInput {...common} clearable value={typeof value === 'string' ? value : undefined} onChange={onChange} />)
      : field.type === 'string'
        ? <TextInput {...common} value={typeof value === 'string' ? value : ''}
            onChange={(e) => onChange(e.currentTarget.value === '' ? undefined : e.currentTarget.value)} />
        : <NumberInput {...common} value={typeof value === 'number' ? value : ''} allowDecimal={field.type === 'number'}
            {...(field.minimum === undefined ? {} : { min: field.minimum })} {...(field.maximum === undefined ? {} : { max: field.maximum })}
            onChange={(v) => onChange(typeof v === 'number' ? v : undefined)} />;

  if (layout === 'stacked') return control;
  return (
    <div className="qube-toolbar-field">
      <Text component="label" htmlFor={id} size="xs" c="dimmed" className="qube-toolbar-label">{label}</Text>
      {control}
    </div>
  );
}

/** The toolbar input's width class: a toolbar input is as wide as what it holds, not as wide as the row. */
function kind(field: ValueSchema): 'choice' | 'date' | 'text' | 'number' {
  if (field.type === 'string' && field.enum !== undefined) return 'choice';
  if (field.type === 'string') return field.format === 'date' ? 'date' : 'text';
  return 'number';
}
