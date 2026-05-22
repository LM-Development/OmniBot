{{/* Default deployment name */}}
{{- define "fullName" -}}
  {{- default $.Release.Name $.Values.global.override.name -}}
{{- end -}}

{{/* Default namespace */}}
{{- define "namespace" -}}
  {{- default $.Release.Namespace $.Values.global.override.namespace -}}
{{- end -}}

{{/* Define ingress-tls secret name */}}
{{- define "ingress.tls.secretName" -}}
  {{- printf "ingress-tls-%s" .Values.ingress.botReleaseName -}}
{{- end -}}

{{/* Check if host is set */}}
{{- define "hostName" -}}
  {{- if .Values.ingress.host -}}
    {{- printf "%s" $.Values.ingress.host -}}
  {{- else -}}
    {{- fail "You need to specify ingress.host" -}}
  {{- end -}}
{{- end -}}
