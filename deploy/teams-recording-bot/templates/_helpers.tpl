{{/* Default deployment name */}}
{{- define "fullName" -}}
  {{- default $.Release.Name $.Values.global.override.name -}}
{{- end -}}

{{/* Default namespace */}}
{{- define "namespace" -}}
  {{- default $.Release.Namespace $.Values.global.override.namespace -}}
{{- end -}}

{{/* Check replicaCount is less than maxReplicaCount */}}
{{- define "maxCount" -}}
  {{- if lt (int $.Values.scale.maxReplicaCount) 1 -}}
    {{- fail "scale.maxReplicaCount cannot be less than 1" -}}
  {{- end -}}
  {{- if gt (int $.Values.scale.replicaCount) (int .Values.scale.maxReplicaCount) -}}
    {{- fail "scale.replicaCount cannot be greater than scale.maxReplicaCount" -}}
  {{- else -}}
    {{- printf "%d" (int $.Values.scale.maxReplicaCount) -}}
  {{- end -}}
{{- end -}}

{{/* Check if issuer email is set */}}
{{- define "cluster-issuer.email" -}}
  {{- if eq $.Values.ingress.tls.email "YOUR_EMAIL" -}}
    {{- fail "You need to specify a ingress tls email for lets encrypt" -}}
  {{- else if $.Values.ingress.tls.email  -}}
    {{- printf "%s" $.Values.ingress.tls.email -}}
  {{- else -}}
    {{- fail "You need to specify a ingress tls email for lets encrypt" -}}
  {{- end -}}
{{- end -}}

{{/*Define ingress-tls secret name*/}}
{{- define "ingress.tls.secretName" -}}
  {{- default (printf "ingress-tls-%s" (include "fullName" .)) $.Values.ingress.tls.secretName -}}    
{{- end -}}

{{/*Define ingress path*/}}
{{- define "ingress.path" -}}
  {{- printf "/%s" (trimPrefix "/" $.Values.ingress.path) -}}    
{{- end -}}

{{/*Define ingress path*/}}
{{- define "ingress.path.withTrailingSlash" -}}
  {{- printf "%s/" (trimSuffix "/" (include "ingress.path" .)) -}}    
{{- end -}}


{{/* Check if host is set */}}
{{- define "hostName" -}}
  {{- if .Values.host -}}
    {{- printf "%s" $.Values.host -}}
  {{- else -}}
    {{- fail "You need to specify a host" -}}
  {{- end -}}
{{- end -}}

{{/* Check if image.domain is set */}}
{{- define "imageDomain" -}}
  {{- if $.Values.image.domain -}}
    {{- printf "%s" $.Values.image.domain -}}
  {{- else -}}
    {{- fail "You need to specify image.domain" -}}
  {{- end -}}
{{- end -}}

{{/* Check if public.ip is set */}}
{{- define "publicIP" -}}
  {{- if $.Values.public.ip -}}
    {{- printf "%s" $.Values.public.ip -}}
  {{- else -}}
    {{- fail "You need to specify public.ip" -}}
  {{- end -}}
{{- end -}}
