{{/* Default deployment name */}}
{{- define "fullName" -}}
  {{- default $.Release.Name $.Values.global.override.name -}}
{{- end -}}

{{/* Default namespace */}}
{{- define "namespace" -}}
  {{- default $.Release.Namespace $.Values.global.override.namespace -}}
{{- end -}}
